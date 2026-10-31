using System;
using System.Collections.Generic;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;

namespace CoreWF.LegacyAdapters
{
    internal static class StandardToolbox
    {
        private static bool initialized;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Ensure(object pane)
        {
            if (initialized) return;
            var package = pane.GetType().GetField("myPackage", Instance).GetValue(pane);
            var helper = package.GetType().GetProperty("ToolboxHelper", Instance).GetValue(package);
            var helperType = helper.GetType();
            var create = helperType.GetMethod("CreateToolboxItem", BindingFlags.Static | BindingFlags.NonPublic);
            var toolbox = (IVsToolbox)helperType.GetField("toolboxService", Instance).GetValue(helper);
            if (toolbox == null) throw new InvalidOperationException("Visual Studio's Toolbox service is not ready.");
            var remove = helperType.GetMethod("RemoveItem", Instance, null, new[] { typeof(string), typeof(string) }, null);
            var resources = (ResourceManager)pane.GetType().Assembly.GetType("Microsoft.VisualStudio.Activities.Resources", true)
                .GetProperty("ResourceManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
            var catalog = new XmlDocument { XmlResolver = null };
            using (var stream = typeof(StandardToolbox).Assembly.GetManifestResourceStream("CoreWF.LegacyAdapters.StandardToolbox.xml"))
                catalog.Load(stream);
            foreach (XmlElement entry in catalog.DocumentElement.ChildNodes)
            {
                string category = entry.GetAttribute("Category");
                string label = entry.GetAttribute("Label");
                var assembly = Assembly.Load(entry.GetAttribute("Assembly"));
                var type = assembly.GetType(entry.GetAttribute("Type"), true);
                var wrapper = create.Invoke(null, new object[] { type.AssemblyQualifiedName, category, assembly.GetName(), label, null, true, ".NETFramework,Version=v4.7.2", label });
                // Use the stock catalog and icons; retain every other/custom entry.
                var icon = resources.GetObject(entry.GetAttribute("Image")) as ICloneable;
                if (icon != null)
                {
                    var bitmap = icon.Clone();
                    bitmap.GetType().GetMethod("MakeTransparent", Type.EmptyTypes).Invoke(bitmap, null);
                    wrapper.GetType().GetProperty("Bitmap", Instance).SetValue(wrapper, bitmap);
                }
                remove.Invoke(helper, new object[] { type.AssemblyQualifiedName, category });
                toolbox.AddTab(category);
                wrapper.GetType().GetMethod("InitializeToolInfo", Instance).Invoke(wrapper, null);
                var data = (IDataObject)wrapper.GetType().GetProperty("ToolData", Instance).GetValue(wrapper);
                var info = (TBXITEMINFO[])wrapper.GetType().GetProperty("ToolInfo", Instance).GetValue(wrapper);
                Marshal.ThrowExceptionForHR(toolbox.AddItem(data, info, category));
            }
            Marshal.ThrowExceptionForHR(toolbox.UpdateToolboxUI());
            var enumerate = helperType.GetMethod("EnumItems", Instance, null, new[] { typeof(string) }, null);
            if (((IList<string>)enumerate.Invoke(helper, new object[] { "Control Flow" })).Count < 10)
                throw new InvalidOperationException("Visual Studio did not retain the standard Control Flow Toolbox items.");
            initialized = true;
        }
    }
}
