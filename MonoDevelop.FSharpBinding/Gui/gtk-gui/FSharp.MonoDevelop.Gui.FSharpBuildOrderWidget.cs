
// This file has been generated by the GUI designer. Do not modify.
namespace MonoDevelop.FSharp.Gui
{
	public partial class FSharpBuildOrderWidget
	{
		private global::Gtk.Frame frame2;
		private global::Gtk.Alignment GtkAlignment;
		private global::Gtk.VBox vbox2;
		private global::Gtk.ScrolledWindow GtkScrolledWindow;
		private global::Gtk.TreeView treeItemList;
		private global::Gtk.HBox hbox1;
		private global::Gtk.Button btnDown;
		private global::Gtk.Button btnUp;
		private global::Gtk.Label GtkLabel2;

		protected virtual void Build ()
		{
			global::Stetic.Gui.Initialize (this);
			// Widget FSharp.MonoDevelop.Gui.FSharpBuildOrderWidget
			global::Stetic.BinContainer.Attach (this);
			this.Name = "FSharp.MonoDevelop.Gui.FSharpBuildOrderWidget";
			// Container child FSharp.MonoDevelop.Gui.FSharpBuildOrderWidget.Gtk.Container+ContainerChild
			this.frame2 = new global::Gtk.Frame ();
			this.frame2.Name = "frame2";
			this.frame2.ShadowType = ((global::Gtk.ShadowType)(0));
			// Container child frame2.Gtk.Container+ContainerChild
			this.GtkAlignment = new global::Gtk.Alignment (0F, 0F, 1F, 1F);
			this.GtkAlignment.Name = "GtkAlignment";
			this.GtkAlignment.LeftPadding = ((uint)(12));
			this.GtkAlignment.TopPadding = ((uint)(6));
			// Container child GtkAlignment.Gtk.Container+ContainerChild
			this.vbox2 = new global::Gtk.VBox ();
			this.vbox2.Name = "vbox2";
			this.vbox2.Spacing = 6;
			// Container child vbox2.Gtk.Box+BoxChild
			this.GtkScrolledWindow = new global::Gtk.ScrolledWindow ();
			this.GtkScrolledWindow.Name = "GtkScrolledWindow";
			this.GtkScrolledWindow.ShadowType = ((global::Gtk.ShadowType)(1));
			// Container child GtkScrolledWindow.Gtk.Container+ContainerChild
			this.treeItemList = new global::Gtk.TreeView ();
			this.treeItemList.HeightRequest = 300;
			this.treeItemList.CanFocus = true;
			this.treeItemList.Name = "treeItemList";
			this.treeItemList.HeadersVisible = false;
			this.GtkScrolledWindow.Add (this.treeItemList);
			this.vbox2.Add (this.GtkScrolledWindow);
			global::Gtk.Box.BoxChild w2 = ((global::Gtk.Box.BoxChild)(this.vbox2 [this.GtkScrolledWindow]));
			w2.Position = 0;
			// Container child vbox2.Gtk.Box+BoxChild
			this.hbox1 = new global::Gtk.HBox ();
			this.hbox1.Name = "hbox1";
			this.hbox1.Spacing = 6;
			// Container child hbox1.Gtk.Box+BoxChild
			this.btnDown = new global::Gtk.Button ();
			this.btnDown.WidthRequest = 60;
			this.btnDown.CanFocus = true;
			this.btnDown.Name = "btnDown";
			this.btnDown.UseUnderline = true;
			this.btnDown.Label = global::Mono.Unix.Catalog.GetString ("Down");
			this.hbox1.Add (this.btnDown);
			global::Gtk.Box.BoxChild w3 = ((global::Gtk.Box.BoxChild)(this.hbox1 [this.btnDown]));
			w3.Position = 1;
			w3.Expand = false;
			w3.Fill = false;
			// Container child hbox1.Gtk.Box+BoxChild
			this.btnUp = new global::Gtk.Button ();
			this.btnUp.WidthRequest = 60;
			this.btnUp.CanFocus = true;
			this.btnUp.Name = "btnUp";
			this.btnUp.UseUnderline = true;
			this.btnUp.Label = global::Mono.Unix.Catalog.GetString ("Up");
			this.hbox1.Add (this.btnUp);
			global::Gtk.Box.BoxChild w4 = ((global::Gtk.Box.BoxChild)(this.hbox1 [this.btnUp]));
			w4.Position = 2;
			w4.Expand = false;
			w4.Fill = false;
			this.vbox2.Add (this.hbox1);
			global::Gtk.Box.BoxChild w5 = ((global::Gtk.Box.BoxChild)(this.vbox2 [this.hbox1]));
			w5.Position = 1;
			w5.Expand = false;
			w5.Fill = false;
			this.GtkAlignment.Add (this.vbox2);
			this.frame2.Add (this.GtkAlignment);
			this.GtkLabel2 = new global::Gtk.Label ();
			this.GtkLabel2.Name = "GtkLabel2";
			this.GtkLabel2.LabelProp = global::Mono.Unix.Catalog.GetString ("<b>Build order</b>");
			this.GtkLabel2.UseMarkup = true;
			this.frame2.LabelWidget = this.GtkLabel2;
			this.Add (this.frame2);
			if ((this.Child != null)) {
				this.Child.ShowAll ();
			}
			this.Hide ();
		}
	}
}
