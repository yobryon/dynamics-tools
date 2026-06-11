using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Schema;
using System.Runtime.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;

class P
{
    static void Main(string[] args)
    {
        var axType = args.Length > 0 ? args[0] : "AxClass";
        var asm = typeof(AxClass).Assembly;
        var t = asm.GetType("Microsoft.Dynamics.AX.Metadata.MetaModel." + axType);
        if (t == null) { Console.Error.WriteLine($"type not found: {axType}"); return; }

        var exporter = new XsdDataContractExporter();
        exporter.Export(t);

        // The exporter emits a schemaset spanning multiple namespaces.
        // Find the schema declaring our root element by walking targetNamespace.
        Console.Error.WriteLine($"=== Schemas in set ===");
        foreach (XmlSchema s in exporter.Schemas.Schemas())
            Console.Error.WriteLine($"  ns='{s.TargetNamespace}'  elements={s.Items.OfType<XmlSchemaElement>().Count()}  complexTypes={s.Items.OfType<XmlSchemaComplexType>().Count()}");

        var outDir = args.Length > 1 ? args[1] : Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false };
        int i = 0;
        foreach (XmlSchema s in exporter.Schemas.Schemas())
        {
            var fname = "schema-" + i + ".xsd";
            var nsLabel = string.IsNullOrEmpty(s.TargetNamespace) ? "(no-namespace)" : s.TargetNamespace;
            var path = Path.Combine(outDir, fname);
            using (var xw = XmlWriter.Create(path, settings)) { s.Write(xw); }
            Console.Error.WriteLine($"  [{i}] {path}  ns={nsLabel}");
            i++;
        }
    }
}
