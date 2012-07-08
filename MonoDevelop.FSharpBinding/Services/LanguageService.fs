// --------------------------------------------------------------------------------------
// Main file - contains types that call F# compiler service in the background, display
// error messages and expose various methods for to be used from MonoDevelop integration
// --------------------------------------------------------------------------------------

namespace MonoDevelop.FSharp
#nowarn "40"

open System
open System.Xml
open System.Text
open System.Threading
open System.Diagnostics

open MonoDevelop.Ide
open MonoDevelop.Core
open MonoDevelop.Projects
open MonoDevelop.Ide.Tasks
open MonoDevelop.Ide.Gui

open ICSharpCode.NRefactory.TypeSystem
open ICSharpCode.NRefactory.Completion

open MonoDevelop.FSharp
open MonoDevelop.FSharp.MailBox

open Microsoft.FSharp.Compiler.SourceCodeServices

module FsParser = Microsoft.FSharp.Compiler.Parser

// --------------------------------------------------------------------------------------

/// Contains settings of the F# language service
module ServiceSettings = 

  /// When making blocking calls from the GUI, we specify this
  /// value as the timeout, so that the GUI is not blocked forever
  let blockingTimeout = 500
  
  /// How often should we trigger the 'OnIdle' event and run
  /// background compilation of the current project?
  let idleTimeout = 3000

  /// When errors are reported, we don't show them immediately (because appearing
  /// bubbles while typing are annoying). We show them when the user doesn't
  /// type anything new into the editor for the time specified here
  let errorTimeout = 1000

// --------------------------------------------------------------------------------------
    
/// Formatting of tool-tip information displayed in F# IntelliSense
module internal TipFormatter = 

  let private buildFormatComment cmt (sb:StringBuilder) = 
    match cmt with
    | XmlCommentText(s) -> sb.AppendLine("<i>" + GLib.Markup.EscapeText(s) + "</i>")
    // For 'XmlCommentSignature' we could get documentation from 'xml' 
    // files, but I'm not sure whether these are available on Mono
    | _ -> sb

  // If 'isSingle' is true (meaning that this is the only tip displayed)
  // then we add first line "Multiple overloads" because MD prints first
  // int in bold (so that no overload is highlighted)
  let private buildFormatElement isSingle el (sb:StringBuilder) =
    match el with 
    | DataTipElementNone -> sb
    | DataTipElement(it, comment) -> 
        sb.AppendLine(GLib.Markup.EscapeText(it)) |> buildFormatComment comment
    | DataTipElementGroup(items) -> 
        let items, msg = 
          if items.Length > 10 then 
            (items |> Seq.take 10 |> List.ofSeq), 
             sprintf "   <i>(+%d other overloads)</i>" (items.Length - 10) 
          else items, null
        if (isSingle && items.Length > 1) then
          sb.AppendLine("Multiple overloads") |> ignore
        for (it, comment) in items do
          sb.AppendLine(GLib.Markup.EscapeText(it)) |> buildFormatComment comment |> ignore
        if msg <> null then sb.AppendFormat(msg) else sb
    | DataTipElementCompositionError(err) -> 
        sb.Append("Composition error: " + GLib.Markup.EscapeText(err))
      
  let private buildFormatTip tip (sb:StringBuilder) = 
    match tip with
    | DataTipText([single]) -> sb |> buildFormatElement true single
    | DataTipText(its) -> 
        sb.AppendLine("Multiple items") |> ignore
        its |> Seq.mapi (fun i it -> i = 0, it) |> Seq.fold (fun sb (first, item) ->
          if not first then sb.AppendLine("\n--------------------\n") |> ignore
          sb |> buildFormatElement false item) sb

  /// Format tool-tip that we get from the language service as string        
  let formatTip tip = 
    (buildFormatTip tip (new StringBuilder())).ToString().Trim('\n', '\r')

  /// Formats tool-tip and turns the first line into heading
  /// MonoDevelop does this automatically for completion data, 
  /// so we do the same thing explicitly for hover tool-tips
  let formatTipWithHeader tip = 
    let str = formatTip tip
    let parts = str.Split([| '\n' |], 2)
    "<b>" + parts.[0] + "</b>" +
      (if parts.Length > 1 then "<small>\n" + parts.[1] + "</small>" else "")
    

// --------------------------------------------------------------------------------------

/// Parsing utilities for IntelliSense (e.g. parse identifier on the left-hand side
/// of the current cursor location etc.)
module Parsing = 
  open FSharp.Parser
  
  /// Parses F# short-identifier (i.e. not including '.'); also ignores active patterns
  let parseIdent =  
    many (sat PrettyNaming.IsIdentifierPartCharacter) |> map String.ofSeq

  /// Parse F# short-identifier and reverse the resulting string
  let parseBackIdent =  
    many (sat PrettyNaming.IsIdentifierPartCharacter) |> map String.ofReversedSeq

  /// Parse remainder of a logn identifier before '.' (e.g. "Name.space.")
  /// (designed to look backwards - reverses the results after parsing)
  let rec parseBackLongIdentRest = parser {
    return! parser {
      let! _ = char '.'
      let! ident = parseBackIdent
      let! rest = parseBackLongIdentRest
      return ident::rest }
    return [] } 
    
  /// Parse long identifier with residue (backwards) (e.g. "Console.Wri")
  /// and returns it as a tuple (reverses the results after parsing)
  let parseBackIdentWithResidue = parser {
    let! residue = many alphanum |> map String.ofReversedSeq
    return! parser {
      let! long = parseBackLongIdentRest
      return residue, long |> List.rev }
    return residue, [] }   

  /// Parse long identifier and return it as a list (backwards, reversed)
  let parseBackLongIdent = parser {
    return! parser {
      let! ident = parseBackIdent
      let! rest = parseBackLongIdentRest
      return ident::rest |> List.rev }
    return [] }

  /// Create sequence that reads the string backwards
  let createBackStringReader (str:string) from = seq { 
    for i in (min from (str.Length - 1)) .. -1 .. 0 do yield str.[i] }

  /// Create sequence that reads the string forwards
  let createForwardStringReader (str:string) from = seq { 
    for i in (max 0 from) .. (str.Length - 1) do yield str.[i] }

  /// Returns first result returned by the parser
  let getFirst p s = apply p s |> List.head
  
    
// --------------------------------------------------------------------------------------

/// Wraps the result of type-checking and provides methods for implementing
/// various IntelliSense functions (such as completion & tool tips)
type internal TypedParseResult(info:TypeCheckInfo) =

  /// Get declarations at the current location in the specified document
  /// (used to implement dot-completion in 'FSharpTextEditorCompletion.fs')
  member x.GetDeclarations(doc:Document) = 
    let lineStr = doc.Editor.GetLineText(doc.Editor.Caret.Line)
    
    // Get the long identifier before the current location
    // 'residue' is the part after the last dot and 'longName' is before
    // e.g.  System.Console.Wri  --> "Wri", [ "System"; "Console"; ]
    let lookBack = Parsing.createBackStringReader lineStr (doc.Editor.Caret.Column - 2)
    let residue, longName = 
      lookBack 
      |> Parsing.getFirst Parsing.parseBackIdentWithResidue
    
    Debug.tracef "Result" "GetDeclarations: column: %d, ident: %A\n    Line: %s" 
      (doc.Editor.Caret.Line - 1) (longName, residue) lineStr
    let res = 
      info.GetDeclarations
        ( (doc.Editor.Caret.Line - 1, doc.Editor.Caret.Column - 1), 
          lineStr, (longName, residue), 0) // 0 is tokenTag, which is ignored in this case

    Debug.tracef "Result" "GetDeclarations: returning %d items" res.Items.Length
    res

  /// Get the tool-tip to be displayed at the specified offset (relatively
  /// from the beginning of the current document)
  member x.GetToolTip(offset:int, doc:Mono.TextEditor.TextDocument) =
    let txt = doc.Text
    let sel = txt.[offset]
    
    let loc  = doc.OffsetToLocation(offset)
    let line, col = loc.Line, loc.Column
    let currentLine = doc.Lines |> Seq.nth loc.Line    
    let lineStr = txt.Substring(currentLine.Offset, currentLine.EndOffset - currentLine.Offset)
    
    // Parsing - find the identifier around the current location
    // (we look for full identifier in the backward direction, but only
    // for a short identifier forward - this means that when you hover
    // 'B' in 'A.B.C', you will get intellisense for 'A.B' module)
    let lookBack = Parsing.createBackStringReader lineStr col
    let lookForw = Parsing.createForwardStringReader lineStr (col + 1)
    
    let backIdent = Parsing.getFirst Parsing.parseBackLongIdent lookBack
    let nextIdent = Parsing.getFirst Parsing.parseIdent lookForw
    
    let currentIdent, identIsland =
      match List.rev backIdent with
      | last::prev -> 
         let current = last + nextIdent
         current, current::prev |> List.rev
      | [] -> "", []

    Debug.tracef 
      "Result" "Get tool tip at %d:%d (offset %d - %d)\nIdentifier: %A (Current: %s) \nLine string: %s"  
      line col currentLine.Offset currentLine.EndOffset identIsland currentIdent lineStr

    match identIsland with
    | [ "" ] -> 
        // There is no identifier at the current location
        DataTipText.Empty 
    | _ -> 
        // Assume that we are inside identifier (F# services can also handle
        // case when we're in a string in '#r "Foo.dll"' but we don't do that)
        let token = FsParser.tagOfToken(FsParser.token.IDENT("")) 
        let res = info.GetDataTipText((line, col), lineStr, identIsland, token)
        match res with
        | DataTipText(elems) 
            when elems |> List.forall (function 
              DataTipElementNone -> true | _ -> false) -> 
          // This works if we're inside "member x.Foo" and want to get 
          // tool tip for "Foo" (but I'm not sure why)
          Debug.tracef "Result" "First attempt returned nothing"   
          let res = info.GetDataTipText((line, col + 2), lineStr, [ currentIdent ], token)
          Debug.tracef "Result" "Returning the result of second try"   
          res
        | _ -> 
          Debug.tracef "Result" "Got something, returning"  
          res 
                               
// --------------------------------------------------------------------------------------

/// Represents request send to the background worker
/// We need information about the current file and project (options)
type internal ParseRequest
    (file:FilePath, source:string, options:CheckOptions, fullCompile:bool) =
  member x.File  = file
  member x.Source = source
  member x.Options = options
  member x.StartFullCompile = fullCompile

// --------------------------------------------------------------------------------------
// Parse worker - we have a single instance of this running in the background and it
// calls the F# compiler service to do parsing of files; The worker can take some time
// to process messages, so if it receives a request while processing, it ignores it
//  (callers can call it as frequently as they want)

/// RequestParse runs ordinary parse, RequestQuickParse tries to get
/// information from the cache maintained by the F# service and reply quickly
type internal ParseWorkerMessage = 
  | RequestParse of ParseRequest
  | RequestQuickParse of ParseRequest
    
/// Runs in background and calls the F# compiler services
/// - reportUntyped - called when new untyped information is available
/// - reportTyped   - called when new typed information is available
/// - reportFailure - called when parsing failed (and 'reportTyped' could not be called)
type internal ParseWorker(checker:InteractiveChecker, reportUntyped, reportTyped, reportFailure) =
  // 
  let maximalQueueLength = 2
  
  let mbox = SimpleMailboxProcessor.Start(fun mbox ->
    async { 
      while true do 
        Debug.tracef "Worker" "Waiting for message"
        let! msg = mbox.Receive()
        Debug.tracef "Worker" "Got message %A" msg 
        
        let ((RequestParse info)|(RequestQuickParse info)) = msg
        let fileName = info.File.FullPath.ToString()        
        try
          match msg with
          | RequestParse(info) ->
              Debug.tracef "Worker" "Request parse received"
              // Run the untyped parsing of the file and report result...
              let untypedInfo = checker.UntypedParse(fileName, info.Source, info.Options)
              reportUntyped(info.File, untypedInfo)
              
              // Now run the type-chekcing
              let fileName = Common.fixFileName(fileName)
              let res = checker.TypeCheckSource( untypedInfo, fileName, 0, info.Source,
                                                 info.Options, IsResultObsolete(fun () -> false) )
              reportTyped(info.File, res)               
              
              // If this is 'full' request, then start background compilations too
              if info.StartFullCompile then
                Debug.tracef "Worker" "Starting background compilations"
                checker.StartBackgroundCompile(info.Options)
              Debug.tracef "Worker" "Parse completed"
              
          | RequestQuickParse(info) ->
              Debug.tracef "Worker" "Request quick parse received"
              // Try to get recent results from the F# service
              match checker.TryGetRecentTypeCheckResultsForFile(fileName, info.Options) with
              | Some(untyped, typed, _) ->
                  Debug.tracef "Worker" "Quick parse completed - success"
                  reportUntyped(info.File, untyped)
                  reportTyped(info.File, TypeCheckSucceeded typed)
              | None ->
                  reportFailure(info)
                  Debug.tracef "Worker" "Quick parse completed - failed"
    
        with e -> 
          Debug.tracef "Worker" "Worker failed!"
          Debug.tracee "Worker" e
          // Exception from the F# service - report to the caller and continue looping
          reportFailure(info) })

  //do mbox.Error.Add(fun e -> 
  //     Debug.tracef "Worker" "ParserWorker agent error"
  //     Debug.tracee "Worker" e )
       
  member x.RequestParse(req) = 
    Debug.tracef "Worker" "RequestParse (#items = %d)" mbox.CurrentQueueLength
    if mbox.CurrentQueueLength <= maximalQueueLength then
      mbox.Post(RequestParse(req))
    else Debug.tracef "Worker" "Ignoring RequestParse"
      
  member x.RequestQuickParse(req) = 
    Debug.tracef "Worker" "RequestParse (#items = %d)" mbox.CurrentQueueLength
    if mbox.CurrentQueueLength <= maximalQueueLength then
      mbox.Post(RequestQuickParse(req))
    else Debug.tracef "Worker" "Ignoring RequestParse"
  
// --------------------------------------------------------------------------------------
// Language service - is a mailbox processor that deals with requests from the user
// interface - mainly to trigger background parsing or get current parsing results
// All processing in the mailbox is quick - however, if we don't have required info
// we post ourselves a message that will be handled when the info becomes available

type internal LanguageServiceMessage = 
  // Trigger parse request in ParserWorker
  | TriggerRequest of ParseRequest
  
  // Messages with new results sent from ParserWorker
  | UpdateUntypedInfo of FilePath * UntypedParseInfo
  | UpdateTypedInfo of FilePath * TypeCheckAnswer
  | UpdateInfoFailed of ParseRequest
  
  // Request for information - when we receive this, we try to send request to
  // ParserWorker (it may be busy) and then send ourselves the 'Done' message
  // which is processed when information become available
  | GetUntypedInfo of ParseRequest * AsyncReplyChannel<UntypedParseInfo>
  | GetTypedInfo of ParseRequest * AsyncReplyChannel<TypedParseResult>
  
  | GetUntypedInfoDone of AsyncReplyChannel<UntypedParseInfo>
  | GetTypedInfoDone of AsyncReplyChannel<TypedParseResult>
  

open System.Reflection
open Microsoft.FSharp.Compiler.Reflection
open ICSharpCode.NRefactory.TypeSystem
open MonoDevelop.Ide.TypeSystem

/// Provides functionality for working with the F# interactive checker running in background
type internal LanguageService private () =

  // Single instance of the language service
  static let instance = Lazy.Create(fun () -> LanguageService())

  // Collection of errors reported by last background check
  let mutable errors : seq<Error> = Seq.empty

  /// Format errors for the given line (if there are multiple, we collapse them into a single one)
  let formatErrorGroup errors = 
    match errors |> List.ofSeq with
    | [error:ErrorInfo] -> 
        // Single error for this line
        let typ = if error.Severity = Severity.Error then ErrorType.Error else ErrorType.Warning
        new Error(typ, error.Message, error.StartLine + 1, error.StartColumn + 1)
    | errors & (first::_) ->        
        // Multiple errors - fold them
        let msg =
          [ for error in errors do
              // Remove line-breaks from messages such as type mismatch (they would look ugly)
              let msg = error.Message.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
              let msg = msg |> Array.map (fun s -> s.Trim()) |> String.concat " "
              yield sprintf "(%d-%d) %s" (error.StartColumn+1) (error.EndColumn+1) msg ]
          |> String.concat "\n"
          
        // Report as error if there is at least one error              
        let typ = if errors |> Seq.forall (fun e -> e.Severity = Severity.Warning) then ErrorType.Warning else ErrorType.Error
        new Error(typ, "Multiple errors\n" + msg, first.StartLine + 1, 1)
    | _ -> failwith "Unexpected" 


  // Are we currently updating the errors list?
  let mutable updatingErrors = false
  
  /// To be called from the language service mailbox processor (on a 
  /// GUI thread!) when new errors are reported for the specified file
  let updateErrors(file:FilePath, currentErrors:array<ErrorInfo>) = 
    Debug.tracef "Errors" "Got update for: %s" (IO.Path.GetFileName(file.FullPath.ToString()))
    
    // MonoDevelop reports only a single error per line, so we group them to report everything
    let grouped = currentErrors |> Seq.groupBy (fun e -> e.StartLine)
    errors <- 
      [ for _, error in grouped do
          yield formatErrorGroup error ]

    // Trigger parse for the file (if it is still the current one)
    updatingErrors <- true
    Debug.tracef "Errors" "Trigger update after completion"
    let doc = IdeApp.Workbench.ActiveDocument
    if doc.FileName.FullPath = file.FullPath then
      TypeSystemService.ParseFile(file.ToString(), doc.Editor.MimeType, doc.Editor.Text) |> ignore
    updatingErrors <- false

  // ------------------------------------------------------------------------------------

  // Create an instance of interactive checker
  let checker = InteractiveChecker.Create(ignore)
  
  // Post message to the 'LanguageService' mailbox
  let rec post m = (mbox:SimpleMailboxProcessor<_>).Post(m)
  // Create a 'ParseWorker' that processes parse requests one-by-one
  and worker = 
    ParseWorker( checker, (UpdateUntypedInfo >> post), 
                 (UpdateTypedInfo >> post), (UpdateInfoFailed >> post) )
  
  // Mailbox of this 'LanguageService'
  and mbox = SimpleMailboxProcessor.Start(fun mbox ->
  
    // Tail-recursive loop that remembers the current state
    // (untyped and typed parse results)
    let rec loop ((untyped, typed) as state) =
      mbox.Scan(fun msg ->
        Debug.tracef "LanguageService" "Checking message %s" (msg.GetType().Name)
        match msg, (untyped, typed) with 
        
        // Try forwarding request to the parser worker
        | TriggerRequest(req), _ -> Some <| async {
            Debug.tracef "LanguageService" "TriggerRequest"
            worker.RequestParse(req)
            return! loop state }

        // If we receive new untyped info, we store it as the current state
        | UpdateUntypedInfo(_, updatedUntyped), _ -> Some <| async { 
            Debug.tracef "LanguageService" "Update untyped info - succeeded"
            return! loop (Some updatedUntyped, typed) }
            
        // If we receive new typed info, we store it as the current state
        | UpdateTypedInfo(file, updatedTyped), _ -> Some <| async { 
            // Construct new typed parse result if the task succeeded
            let newRes =
              match updatedTyped with
              | TypeCheckSucceeded(results) ->
                  // Handle errors on the GUI thread
                  Debug.tracef "LanguageService" "Update typed info - is some? %A" results.TypeCheckInfo.IsSome
                  DispatchService.GuiDispatch(fun () -> updateErrors(file, results.Errors))
                  match results.TypeCheckInfo with
                  | Some(info) -> Some(TypedParseResult(info))
                  | _ -> typed
              | _ -> 
                  Debug.tracef "LanguageService" "Update typed info - failed"
                  typed
            return! loop (untyped, newRes) }

        // If the parser worker failed, we ignore it (we could retry?)
        | UpdateInfoFailed(retryReq), _ -> Some <| async { 
            Debug.tracef "LanguageService" "Update info failed"
            return! loop state }
        
        
        // When we receive request for information and we don't have it we trigger a 
        // parse request and then send ourselves a message, so that we can reply later
        | GetUntypedInfo(quickReq, reply), (unty, _) -> Some <| async {
            Debug.tracef "LanguageService" "GetUntypedInfo"
            if unty = None then worker.RequestQuickParse(quickReq)
            post(GetUntypedInfoDone(reply)) 
            return! loop state }
        
        | GetTypedInfo(quickReq, reply), (_, ty) -> Some <| async { 
            Debug.tracef "LanguageService" "GetTypedInfo"
            if ty = None then worker.RequestQuickParse(quickReq)
            post(GetTypedInfoDone(reply)) 
            return! loop state }

        
        // If we have the information, we reply to the sender (hopefuly, he is still there)
        | GetUntypedInfoDone(reply), (Some untypedRes, _) -> Some <| async {
            Debug.tracef "LanguageService" "GetUntypedInfoDonw"
            reply.Reply(untypedRes)
            return! loop state }
            
        | GetTypedInfoDone(reply), (_, Some typedRes) -> Some <| async {
            Debug.tracef "LanguageService" "GetTypedInfoDone"
            reply.Reply(typedRes)
            return! loop state }

        // We didn't have information to reply to a request - keep waiting for results!
        | _ -> 
            Debug.tracef "Worker" "No match found for the message"
            None )
        
    // Start looping with no initial information        
    loop (None, None) )

  // do mbox.Error.Add(fun e -> 
  //      Debug.tracef "Worker" "LanguageService agent error"
  //      Debug.tracee "Worker" e )
  
  /// Constructs options for the interactive checker
  member x.GetCheckerOptions(fileName, source, dom:Document, config:ConfigurationSelector) =
    let ext = IO.Path.GetExtension(fileName)
    let opts = 
      if (dom = null || dom.Project = null || ext = ".fsx" || ext = ".fsscript") then
      
        // We are in a stand-alone file (assuming it is a script file) or we
        // are in a project, but currently editing a script file
        try
#if CUSTOM_SCRIPT_RESOLUTION
          // In some versions of F# InteractiveChecker doesn't implement assembly resolution
          // correct on Mono (it throws), so this is a workaround that we can use
          if Environment.runningOnMono then
            // The workaround works pretty well, but we use it only on Mono, because
            // it doesn't implement full MS BUILD resolution (e.g. Reference Assemblies),
            // which is fine on Mono, but could be trouble on Windows
            ScriptOptions.getScriptOptionsForFile(fileName, source)
          else
            checker.GetCheckOptionsFromScriptRoot(fileName, source)
#else
          // TODO: In an early version, the InteractiveChecker resolution doesn't sometimes
          // include FSharp.Core and other essential assemblies, so we need to include them by hand
          let fileName = Common.fixFileName(fileName)
          Debug.tracef "Checkoptions" "Creating for file: '%s'" fileName 
          let opts = checker.GetCheckOptionsFromScriptRoot(fileName, source)
          if opts.ProjectOptions |> Seq.exists (fun s -> s.Contains("FSharp.Core.dll")) then opts
          else 
            // Add assemblies that may be missing in the standard assembly resolution
            Debug.tracef "Checkoptions" "Adding missing core assemblies."
            let dirs = ScriptOptions.getDefaultDirectories None []
            opts.WithOptions 
              [| yield! opts.ProjectOptions; 
                 match ScriptOptions.resolveAssembly dirs "FSharp.Core" with
                 | Some fn -> yield sprintf "-r:%s" fn
                 | None -> Debug.tracef "Resolution" "FSharp.Core assembly resolution failed!"
                 match ScriptOptions.resolveAssembly dirs "FSharp.Compiler.Interactive.Settings" with
                 | Some fn -> yield sprintf "-r:%s" fn
                 | None -> Debug.tracef "Resolution" "FSharp.Compiler.Interactive.Settings assembly resolution failed!" |]
#endif        
        with e ->
          failwithf "Exception when getting check options for '%s'\n.Details: %A" fileName e
          
      // We are in a project - construct options using current properties
      else
        let projFile = dom.Project.FileName.ToString()
        let files = Common.getSourceFiles(dom.Project.Items) |> Array.ofSeq
        
        // Read project configuration (compiler & build)
        let projConfig = dom.Project.GetConfiguration(config) :?> DotNetProjectConfiguration
        let fsbuild = projConfig.ProjectParameters :?> FSharpProjectParameters
        let fsconfig = projConfig.CompilationParameters :?> FSharpCompilerParameters
        
        // Order files using the configuration settings & get options
        let shouldWrap = false //It is unknown if the IntelliSense fails to load assemblies with wrapped paths.
        let args = Common.generateCompilerOptions fsconfig dom.Project.Items config shouldWrap
        let root = System.IO.Path.GetDirectoryName(dom.Project.FileName.FullPath.ToString())
        let files = Common.getItemsInOrder root files fsbuild.BuildOrder false |> Array.ofSeq
        CheckOptions.Create(projFile, files, args, false, false) 

    // Print contents of check option for debugging purposes
    Debug.tracef "Checkoptions" "ProjectFileName: %s, ProjectFileNames: %A, ProjectOptions: %A, IsIncompleteTypeCheckEnvironment: %A, UseScriptResolutionRules: %A" 
                 opts.ProjectFileName opts.ProjectFileNames opts.ProjectOptions 
                 opts.IsIncompleteTypeCheckEnvironment opts.UseScriptResolutionRules
    opts
  
  member x.TriggerParse(file:FilePath, src, dom:Document, config, ?full) = 
    let fileName = file.FullPath.ToString()
    let opts = x.GetCheckerOptions(fileName, src, dom, config)
    Debug.tracef "Parsing" "Trigger parse (fileName=%s)" fileName
    mbox.Post(TriggerRequest(ParseRequest(file, src, opts, defaultArg full false)))

  member x.GetTypedParseResult((file:FilePath, src, dom:Document, config), ?timeout) = 
    let fileName = file.FullPath.ToString()
    let opts = x.GetCheckerOptions(fileName, src, dom, config)
    Debug.tracef "Parsing" "Get typed parse result (fileName=%s)" fileName
    let req = ParseRequest(file, src, opts, false)
    mbox.PostAndReply((fun repl -> GetTypedInfo(req, repl)), ?timeout = timeout)
  
  /// Returns a sequence of errors generated by the last background type-check  
  member x.Errors = errors
  /// Are we currently in the process of updating errors?
  member x.UpdatingErrors = updatingErrors
  
  /// Single instance of the language service
  static member Service = instance.Value
    
// --------------------------------------------------------------------------------------

/// Various utilities for working with F# language service
module internal ServiceUtils =
  let map =           
    [ 0x0000, "md-class"; 0x0003, "md-enum"; 0x00012, "md-struct";
      0x00018, "md-struct" (* value type *); 0x0002, "md-delegate"; 0x0008, "md-interface";
      0x000e, "md-class" (* module *); 0x000f, "md-name-space"; 0x000c, "md-method";
      0x000d, "md-extensionmethod" (* method2 ? *); 0x00011, "md-property";
      0x0005, "md-event"; 0x0007, "md-field" (* fieldblue ? *);
      0x0020, "md-field" (* fieldyellow ? *); 0x0001, "md-field" (* const *);
      0x0004, "md-property" (* enummember *); 0x0006, "md-class" (* exception *);
      0x0009, "md-text-file-icon" (* TextLine *); 0x000a, "md-regular-file" (* Script *);
      0x000b, "Script" (* Script2 *); 0x0010, "md-tip-of-the-day" (* Formula *);
      0x00013, "md-class" (* Template *); 0x00014, "md-class" (* Typedef *);
      0x00015, "md-class" (* Type *); 0x00016, "md-struct" (* Union *);
      0x00017, "md-field" (* Variable *); 0x00019, "md-class" (* Intrinsic *);
      0x0001f, "md-breakpint" (* error *); 0x00021, "md-misc-files" (* Misc1 *);
      0x0022, "md-misc-files" (* Misc2 *); 0x00023, "md-misc-files" (* Misc3 *); ] |> Map.ofSeq 

  /// Translates icon code that we get from F# language service into a MonoDevelop icon
  let getIcon glyph =
    match map.TryFind (glyph / 6), map.TryFind (glyph % 6) with  
    | Some(s), _ -> s // Is the second number good for anything?
    | _, _ -> "md-breakpoint" 
  
  
