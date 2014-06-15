namespace MonoDevelop.FSharp

open System
open System.Collections.Generic

open MonoDevelop.Core
open MonoDevelop.Ide
open MonoDevelop.Ide.FindInFiles
open System.Linq
open ICSharpCode.NRefactory.TypeSystem
open System.IO
open MonoDevelop.Ide.TypeSystem
open ICSharpCode.NRefactory.Semantics
open Mono.TextEditor
open System.Threading
open MonoDevelop.Projects

/// Top-level 'ReferenceFinder' extension, referenced in FSharpBinding.addin.xml.orig
type FSharpReferenceFinder() =
    inherit ReferenceFinder()

    /// Detect a symbol that has some region information
    let (|SymbolWithRegion|_|) (x:obj) =
        match x with 
        | :? IVariable as e -> Some e.Region
        | :? IEntity as e -> Some e.Region
        | _ -> None

    /// Detect a symbol that has come from the F# resolver and/or reference finder.
    let (|SymbolWithFSharpInfo|_|) (x:obj) =
        match x with 
        | :? NRefactory.IHasFSharpSymbol as e -> Some e
        | :? IMember as e -> 
            match e.UnresolvedMember with 
            | :? NRefactory.IHasFSharpSymbol as e -> Some e
            | _ -> None
        | _ -> None

    override x.FindReferences(project, projectContent, files, progressMonitor, symbols) =
      // Debugging: Set a breakpoint here if you need to debug.  
      //
      // If the breakpoint is not triggered by a 'Find References' action,
      // then it probably means the inferred set of files to search for a symbol has not been correctly determined
      // by MD/XS.  The logic of 'what to search' used by XS is quite convoluted and depends on properties of
      // the symbol, e.g. accessibility, whether it is an IVariable, IEntity, etc.
      System.Diagnostics.Debug.WriteLine("Finding references...")
      seq { 
        for symbol in symbols do 
          match symbol with
          | SymbolWithRegion(region) & SymbolWithFSharpInfo(fsSymbol) ->
            
            // Get the active document, but only to 
            //   (a) save it
            //   (b) determine if this is a script
            //   (c) find the active project confifuration. 
            // TODO: we should instead enumerate the items in 'files'?
            let activeDoc = IdeApp.Workbench.ActiveDocument
            let activeDocFileName = activeDoc.FileName.FileName

            // Save the active document if dirty: FSharp.Compiler.Service 'GetUsesOfSymbol' currently reads files from disk.
            if activeDoc.IsDirty then activeDoc.Save()

            // Get the source, but only in order to infer the project options for a script.
            let activeDocSource = activeDoc.Editor.Text
            
            let projectFilename, projectFiles, projectArgs, projectFramework = MonoDevelop.getCheckerArgsFromProject(project :?> DotNetProject, IdeApp.Workspace.ActiveConfiguration)
            let references = 
                try Some(MDLanguageService.Instance.GetUsesOfSymbolInProject(projectFilename, activeDocFileName, activeDocSource, projectFiles, projectArgs, projectFramework, fsSymbol.FSharpSymbol) 
                    |> Async.RunSynchronously)
                with _ -> None

            match references with
            | Some(references) -> 
                let memberRefs = [| for symbolUse in references -> 
                                        let text = Mono.TextEditor.Utils.TextFileUtility.ReadAllText(symbolUse.FileName)
                                        NRefactory.createMemberReference(projectContent, symbolUse, symbolUse.FileName, text, fsSymbol.LastIdent) |]
                yield! memberRefs
            | None -> ()

          | _ -> () }
             
