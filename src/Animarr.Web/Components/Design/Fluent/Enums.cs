// Drop-in replacements for Microsoft.FluentUI.AspNetCore.Components enums.
// Existing callsites pass Appearance.Accent, Typography.H3, etc. — keeping
// the same names means no callsite changes when we remove the Fluent package.

namespace Microsoft.FluentUI.AspNetCore.Components;

public enum Appearance
{
    Neutral = 0,
    Accent,
    Lightweight,
    Outline,
    Stealth,
    Filled,
    Hypertext,
}

public enum Typography
{
    Body,
    Subject,
    Header,
    PaneHeader,
    EmailHeader,
    PageTitle,
    HeroTitle,
    H1, H2, H3, H4, H5, H6,
}

public enum DesignThemeModes
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public enum OfficeColor
{
    Default = 0,
    Access, Booking, Bookings, Edge, Excel, Exchange, Lync, Office, OneDrive,
    OneNote, Outlook, Planner, PowerApps, PowerBI, PowerPoint, Project, Publisher,
    SharePoint, Skype, Stream, Sway, Teams, Visio, Windows, Word, Yammer,
}

public enum TooltipPosition
{
    Top, Right, Bottom, Left,
    Start, End,
    TopStart, TopEnd, BottomStart, BottomEnd,
}

// NavLinkMatch intentionally not redefined — Microsoft.AspNetCore.Components.Routing
// already provides one. Our FluentNavLink shim consumes that built-in enum.

public enum Orientation
{
    Horizontal,
    Vertical,
}

public enum VerticalAlignment
{
    Center, Top, Bottom, Stretch,
}

public enum HorizontalAlignment
{
    Start, Center, End, Stretch,
    Left, Right,
    Space,
}

public enum Align
{
    Start, Center, End,
}

public enum DataGridRowSize
{
    Small, Medium, Large,
}

public enum ToastIntent
{
    Success, Warning, Error, Info, Progress, Upload, Download, Event, Mention, Custom,
}

public enum TextFieldType
{
    Text, Email, Password, Tel, Url, Search, Number,
}
