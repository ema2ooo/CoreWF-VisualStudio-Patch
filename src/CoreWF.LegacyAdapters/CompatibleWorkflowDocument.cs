using System;
using System.Activities.Presentation;
using System.Activities.Presentation.Hosting;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CoreWF.LegacyAdapters
{
    public static class CompatibleWorkflowDocument
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        public static void Load(WorkflowDesigner designer, string path)
        {
            string text = File.ReadAllText(path);
            bool coreNamespaces = text.Contains(";assembly=System.Private.CoreLib");
            bool oldSettings = text.Contains("VisualBasic.Settings=\"");
            if (coreNamespaces || oldSettings)
                designer.Context.Services.Publish<IDocumentPersistenceService>(new Persistence(designer, coreNamespaces));
            // Keep the original file-loading path, including its trust check and error handling.
            designer.Load(path);
        }

        private sealed class Persistence : IDocumentPersistenceService
        {
            private readonly WorkflowDesigner designer;
            private readonly bool coreNamespaces;
            public Persistence(WorkflowDesigner designer, bool coreNamespaces) { this.designer = designer; this.coreNamespaces = coreNamespaces; }

            public object Load(string fileName)
            {
                string text = File.ReadAllText(fileName).Replace(";assembly=System.Private.CoreLib", ";assembly=mscorlib");
                text = Regex.Replace(text, "\\s+VisualBasic\\.Settings=\"[^\"]*\"", "");
                typeof(WorkflowDesigner).GetField("loadedFile", PrivateInstance).SetValue(designer, fileName);
                designer.Context.Items.SetValue(new WorkflowFileItem { LoadedFile = fileName });
                designer.Text = text;
                var deserialize = typeof(WorkflowDesigner).GetMethods(PrivateInstance).Single(method => method.Name == "DeserializeString" && method.GetParameters().Length == 3);
                object[] args = { text, null, null };
                object root = deserialize.Invoke(designer, args);
                if (args[1] is IList errors && errors.Count > 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Cast<object>().Select(error => error.GetType().GetProperty("Message")?.GetValue(error) ?? error)));
                return root;
            }

            public void Flush(object documentRoot)
            {
                typeof(WorkflowDesigner).GetMethod("WriteModelToText", PrivateInstance).Invoke(designer, new object[] { null });
                if (coreNamespaces) designer.Text = designer.Text.Replace(";assembly=mscorlib", ";assembly=System.Private.CoreLib");
            }

            public void OnModelChanged(object documentRoot) { }
        }
    }
}
