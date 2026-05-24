// Stub Icons class hierarchy — mirrors the surface area of
// Microsoft.FluentUI.AspNetCore.Components.Icons used in this project.
// Each leaf is a record carrying the design icon name + size; our custom
// FluentIcon component renders that as a <DIcon ... />, no behavioural change.
//
// Mapping table maintained in IconValue.Map (one place to change if a
// callsite uses an icon we haven't mapped yet — it falls back to the
// default `info` glyph so nothing crashes).

namespace Microsoft.FluentUI.AspNetCore.Components;

public class Icon
{
    public string Name { get; init; } = "info";
    public int    Size { get; init; } = 16;
    /// <summary>Maps the Fluent System Icons name (e.g. "VideoClip") to our DIcon name.</summary>
    internal static string Map(string fluentName) => fluentName switch
    {
        "VideoClip"             => "video",
        "ArrowDownload"         => "download",
        "ArrowUpload"           => "upload",
        "FolderOpen"            => "folder",
        "Folder"                => "folder",
        "History"               => "clock",
        "Clock"                 => "clock",
        "Settings"              => "settings",
        "Add"                   => "plus",
        "Subtract"              => "x",
        "ArrowSync"             => "refresh",
        "ArrowClockwise"        => "refresh",
        "ArrowCounterclockwise" => "undo",
        "ArrowLeft"             => "chev-l",
        "ArrowRight"            => "chev-r",
        "ArrowUp"               => "chev-d",
        "ArrowDown"             => "chev-d",
        "Delete"                => "trash",
        "DeleteDismiss"         => "trash",
        "Edit"                  => "pencil",
        "Search"                => "search",
        "DocumentSearch"        => "search",
        "Play"                  => "play",
        "Pause"                 => "pause",
        "Stop"                  => "x",
        "Dismiss"               => "x",
        "Checkmark"             => "check",
        "CheckmarkCircle"       => "check",
        "ChevronUp"             => "chev-d",
        "ChevronDown"           => "chev-d",
        "ChevronLeft"           => "chev-l",
        "ChevronRight"          => "chev-r",
        "Sparkle"               => "sparkle",
        "Image"                 => "image",
        "ImageOff"              => "image",
        "HardDrive"             => "file",
        "ClosedCaption"         => "file",
        "Document"              => "file",
        "Rename"                => "pencil",
        "Open"                  => "external",
        "Warning"               => "warn",
        "Info"                  => "info",
        "ZoomIn"                => "search",
        _                       => "info",
    };
}

public static class Icons
{
    // We expose nested classes that mirror what the callsites use:
    //   new Icons.Regular.Size20.VideoClip()  →  Icon { Name="video",  Size=20 }
    //   new Icons.Filled.Size32.Play()        →  Icon { Name="play",   Size=32 }
    //
    // Adding new icon usages: just declare the class here following the pattern.

    public static class Regular
    {
        public static class Size16
        {
            public class VideoClip            : Icon { public VideoClip()            { Name = Icon.Map("VideoClip");            Size = 16; } }
            public class ArrowDownload        : Icon { public ArrowDownload()        { Name = Icon.Map("ArrowDownload");        Size = 16; } }
            public class FolderOpen           : Icon { public FolderOpen()           { Name = Icon.Map("FolderOpen");           Size = 16; } }
            public class Folder               : Icon { public Folder()               { Name = Icon.Map("Folder");               Size = 16; } }
            public class History              : Icon { public History()              { Name = Icon.Map("History");              Size = 16; } }
            public class Settings             : Icon { public Settings()             { Name = Icon.Map("Settings");             Size = 16; } }
            public class Add                  : Icon { public Add()                  { Name = Icon.Map("Add");                  Size = 16; } }
            public class ArrowSync            : Icon { public ArrowSync()            { Name = Icon.Map("ArrowSync");            Size = 16; } }
            public class ArrowUndo            : Icon { public ArrowUndo()            { Name = Icon.Map("ArrowUndo");            Size = 16; } }
            public class ArrowCounterclockwise: Icon { public ArrowCounterclockwise(){ Name = Icon.Map("ArrowCounterclockwise");Size = 16; } }
            public class Checkmark            : Icon { public Checkmark()            { Name = Icon.Map("Checkmark");            Size = 16; } }
            public class CheckmarkCircle      : Icon { public CheckmarkCircle()      { Name = Icon.Map("CheckmarkCircle");      Size = 16; } }
            public class ChevronUp            : Icon { public ChevronUp()            { Name = Icon.Map("ChevronUp");            Size = 16; } }
            public class ChevronDown          : Icon { public ChevronDown()          { Name = Icon.Map("ChevronDown");          Size = 16; } }
            public class ChevronLeft          : Icon { public ChevronLeft()          { Name = Icon.Map("ChevronLeft");          Size = 16; } }
            public class ChevronRight         : Icon { public ChevronRight()         { Name = Icon.Map("ChevronRight");         Size = 16; } }
            public class Delete               : Icon { public Delete()               { Name = Icon.Map("Delete");               Size = 16; } }
            public class Dismiss              : Icon { public Dismiss()              { Name = Icon.Map("Dismiss");              Size = 16; } }
            public class DocumentSearch       : Icon { public DocumentSearch()       { Name = Icon.Map("DocumentSearch");       Size = 16; } }
            public class Edit                 : Icon { public Edit()                 { Name = Icon.Map("Edit");                 Size = 16; } }
            public class Open                 : Icon { public Open()                 { Name = Icon.Map("Open");                 Size = 16; } }
            public class Rename               : Icon { public Rename()               { Name = Icon.Map("Rename");               Size = 16; } }
            public class Sparkle              : Icon { public Sparkle()              { Name = Icon.Map("Sparkle");              Size = 16; } }
            public class Play                 : Icon { public Play()                 { Name = Icon.Map("Play");                 Size = 16; } }
            public class Pause                : Icon { public Pause()                { Name = Icon.Map("Pause");                Size = 16; } }
            public class Stop                 : Icon { public Stop()                 { Name = Icon.Map("Stop");                 Size = 16; } }
        }
        public static class Size20
        {
            public class VideoClip            : Icon { public VideoClip()            { Name = Icon.Map("VideoClip");            Size = 20; } }
            public class ArrowDownload        : Icon { public ArrowDownload()        { Name = Icon.Map("ArrowDownload");        Size = 20; } }
            public class FolderOpen           : Icon { public FolderOpen()           { Name = Icon.Map("FolderOpen");           Size = 20; } }
            public class History              : Icon { public History()              { Name = Icon.Map("History");              Size = 20; } }
            public class Settings             : Icon { public Settings()             { Name = Icon.Map("Settings");             Size = 20; } }
            public class Add                  : Icon { public Add()                  { Name = Icon.Map("Add");                  Size = 20; } }
            public class ArrowCounterclockwise: Icon { public ArrowCounterclockwise(){ Name = Icon.Map("ArrowCounterclockwise");Size = 20; } }
            public class ArrowDownload2       : Icon { public ArrowDownload2()       { Name = Icon.Map("ArrowDownload");        Size = 20; } }
            public class ArrowLeft            : Icon { public ArrowLeft()            { Name = Icon.Map("ArrowLeft");            Size = 20; } }
            public class ArrowSync            : Icon { public ArrowSync()            { Name = Icon.Map("ArrowSync");            Size = 20; } }
            public class ArrowUp              : Icon { public ArrowUp()              { Name = Icon.Map("ArrowUp");              Size = 20; } }
            public class Checkmark            : Icon { public Checkmark()            { Name = Icon.Map("Checkmark");            Size = 20; } }
            public class ClosedCaption        : Icon { public ClosedCaption()        { Name = Icon.Map("ClosedCaption");        Size = 20; } }
            public class Delete               : Icon { public Delete()               { Name = Icon.Map("Delete");               Size = 20; } }
            public class DeleteDismiss        : Icon { public DeleteDismiss()        { Name = Icon.Map("DeleteDismiss");        Size = 20; } }
            public class Dismiss              : Icon { public Dismiss()              { Name = Icon.Map("Dismiss");              Size = 20; } }
            public class Document             : Icon { public Document()             { Name = Icon.Map("Document");             Size = 20; } }
            public class DocumentSearch       : Icon { public DocumentSearch()       { Name = Icon.Map("DocumentSearch");       Size = 20; } }
            public class Edit                 : Icon { public Edit()                 { Name = Icon.Map("Edit");                 Size = 20; } }
            public class Filter               : Icon { public Filter()               { Name = Icon.Map("Filter");               Size = 20; } }
            public class HardDrive            : Icon { public HardDrive()            { Name = Icon.Map("HardDrive");            Size = 20; } }
            public class Image                : Icon { public Image()                { Name = Icon.Map("Image");                Size = 20; } }
            public class ImageOff             : Icon { public ImageOff()             { Name = Icon.Map("ImageOff");             Size = 20; } }
            public class Pause                : Icon { public Pause()                { Name = Icon.Map("Pause");                Size = 20; } }
            public class Play                 : Icon { public Play()                 { Name = Icon.Map("Play");                 Size = 20; } }
            public class Rename               : Icon { public Rename()               { Name = Icon.Map("Rename");               Size = 20; } }
            public class Search               : Icon { public Search()               { Name = Icon.Map("Search");               Size = 20; } }
            public class Sparkle              : Icon { public Sparkle()              { Name = Icon.Map("Sparkle");              Size = 20; } }
            public class Stop                 : Icon { public Stop()                 { Name = Icon.Map("Stop");                 Size = 20; } }
            public class Video                : Icon { public Video()                { Name = Icon.Map("VideoClip");            Size = 20; } }
            public class Warning              : Icon { public Warning()              { Name = Icon.Map("Warning");              Size = 20; } }
            public class ZoomIn               : Icon { public ZoomIn()               { Name = Icon.Map("ZoomIn");               Size = 20; } }
        }
        public static class Size28
        {
            public class Checkmark            : Icon { public Checkmark()            { Name = Icon.Map("Checkmark");            Size = 28; } }
            public class Image                : Icon { public Image()                { Name = Icon.Map("Image");                Size = 28; } }
            public class VideoClip            : Icon { public VideoClip()            { Name = Icon.Map("VideoClip");            Size = 28; } }
        }
        public static class Size24
        {
            public class FolderOpen           : Icon { public FolderOpen()           { Name = Icon.Map("FolderOpen");           Size = 24; } }
            public class Image                : Icon { public Image()                { Name = Icon.Map("Image");                Size = 24; } }
            public class Play                 : Icon { public Play()                 { Name = Icon.Map("Play");                 Size = 24; } }
            public class ZoomIn               : Icon { public ZoomIn()               { Name = Icon.Map("ZoomIn");               Size = 24; } }
        }
        public static class Size32
        {
            public class ClosedCaption        : Icon { public ClosedCaption()        { Name = Icon.Map("ClosedCaption");        Size = 32; } }
            public class DocumentSearch       : Icon { public DocumentSearch()       { Name = Icon.Map("DocumentSearch");       Size = 32; } }
            public class Folder               : Icon { public Folder()               { Name = Icon.Map("Folder");               Size = 32; } }
            public class Play                 : Icon { public Play()                 { Name = Icon.Map("Play");                 Size = 32; } }
        }
        public static class Size48
        {
            public class Folder               : Icon { public Folder()               { Name = Icon.Map("Folder");               Size = 48; } }
            public class VideoClip            : Icon { public VideoClip()            { Name = Icon.Map("VideoClip");            Size = 48; } }
        }
    }

    public static class Filled
    {
        public static class Size32
        {
            public class Play                 : Icon { public Play()                 { Name = Icon.Map("Play");                 Size = 32; } }
        }
    }
}

/// <summary>Stub Color enum used by `Color="Color.Lightweight"` callsites.</summary>
public enum Color
{
    Neutral,
    Accent,
    Lightweight,
    Disabled,
    Custom,
}
