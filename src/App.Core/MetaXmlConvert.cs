using System;
using System.Collections.Generic;
using System.Xml;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Converts a meta/resource XML back to its binary form, robust to the file name.
/// CodeWalker's <see cref="XmlMeta.GetXMLFormat"/> picks the format purely from the
/// extension and DEFAULTS to RSC for any plain ".xml". Feeding non-RSC content (a PSO
/// meta like handling/vehicles/carcols) to the RSC path throws a bare
/// NullReferenceException ("Object reference not set to an instance of an object").
///
/// We try the name-derived format first (an explicit .pso.xml/.ytd.xml is trusted), and
/// ONLY when that was the ambiguous RSC default do we also try the other text-meta
/// formats (PSO, then RBF) by content. A wrong format returns null (not garbage) or
/// throws — both are caught — so a mismatch yields a clear, actionable error instead of
/// a raw NRE, and a misnamed-but-valid meta now imports.
/// </summary>
public static class MetaXmlConvert
{
    /// <summary>Converted bytes, or null with <paramref name="error"/> set.</summary>
    public static byte[]? Convert(string xml, string name, string? ddsDir, out string? error)
    {
        error = null;
        var doc = new XmlDocument();
        try { doc.LoadXml(xml ?? ""); }
        catch (Exception ex) { error = "invalid XML: " + ex.Message; return null; }

        var primary = XmlMeta.GetXMLFormat((name ?? "").ToLowerInvariant(), out _);

        var candidates = new List<MetaFormat> { primary };
        if (primary == MetaFormat.RSC)            // ambiguous name -> also try the common metas by content
        {
            candidates.Add(MetaFormat.PSO);
            candidates.Add(MetaFormat.RBF);
        }

        Exception? last = null;
        foreach (var fmt in candidates)
        {
            try
            {
                var data = XmlMeta.GetData(doc, fmt, ddsDir ?? "");
                if (data != null && data.Length > 0) return data;
            }
            catch (Exception ex) { last = ex; }   // wrong format can NRE; try the next candidate
        }

        error = candidates.Count > 1
            ? "couldn't convert this XML — it doesn't parse as an RSC/PSO/RBF meta. If it's a specific "
              + "type, name the file with its full extension (e.g. name.ymt.pso.xml) so the format is unambiguous."
              + (last != null ? $" [{last.Message}]" : "")
            : $"couldn't convert this XML as {primary} (inferred from the name \"{name}\"). "
              + "If that type is wrong, rename it with the matching extension (.pso.xml / .rbf.xml / .ytd.xml …)."
              + (last != null ? $" [{last.Message}]" : "");
        return null;
    }
}
