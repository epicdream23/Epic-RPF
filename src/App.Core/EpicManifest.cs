using System.Collections.Generic;

namespace App.Core;

/// <summary>
/// The manifest inside a <c>.epic</c> extension package — describes what the
/// installer should do to the game files. JSON; one <see cref="EpicOp"/> per action.
/// </summary>
public sealed class EpicManifest
{
    public string Format { get; set; } = "epic/1";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = "";
    public string Target { get; set; } = "update.rpf";   // informational scope hint
    public List<EpicOp> Operations { get; set; } = new();
}

/// <summary>
/// A single install action. <see cref="Op"/> selects the kind; the other fields are
/// used as that kind needs them:
///   replaceFile  target, source (payload entry)            — write/replace a whole file
///   deleteFile   target                                    — remove a file
///   xml          target, action(add|replace|remove|settext), xpath, xml/value, append
///   text         target, action(append|insertBefore|insertAfter|replace|delete), find, value
/// A target is a GTA-root-relative vpath (may be inside .rpf archives). xml/text ops
/// transparently convert binary metas to/from CodeWalker XML.
/// </summary>
public sealed class EpicOp
{
    public string Op { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Source { get; set; }       // replaceFile: payload entry name
    public bool CreateIfMissing { get; set; } = true;

    public string? Action { get; set; }        // xml/text sub-action
    public string? Xpath { get; set; }         // xml
    public string? Xml { get; set; }           // xml: node markup to add/replace
    public string? Attr { get; set; }          // xml setattr: attribute name (meta scalars use value="...")
    public bool Append { get; set; } = true;   // xml add: append (true) vs prepend (false)
    public string? Find { get; set; }          // text: line/substring to locate
    public string? Value { get; set; }         // xml settext / text insert/replace payload
    public string? Note { get; set; }          // optional human description for the preview
}
