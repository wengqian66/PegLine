using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PegLine.Services;

/// <summary>
/// Starts a shell file drag without enabling WinForms for the whole WinUI project.
/// Loads System.Windows.Forms at runtime when present.
/// </summary>
internal static class FileDrag
{
    public static void Begin(IntPtr hwnd, string path)
    {
        var forms = Assembly.Load("System.Windows.Forms, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        var dataType = forms.GetType("System.Windows.Forms.DataObject", throwOnError: true)!;
        var effectsType = forms.GetType("System.Windows.Forms.DragDropEffects", throwOnError: true)!;
        var controlType = forms.GetType("System.Windows.Forms.Control", throwOnError: true)!;

        var data = Activator.CreateInstance(dataType)!;
        var files = new StringCollection { path };
        dataType.GetMethod("SetFileDropList")!.Invoke(data, [files]);

        var copy = Enum.Parse(effectsType, "Copy");
        var move = Enum.Parse(effectsType, "Move");
        var effects = (Enum)Enum.ToObject(effectsType, Convert.ToInt32(copy) | Convert.ToInt32(move));

        // DoDragDrop is an instance method on Control; use a throwaway native window via reflection helper.
        var method = controlType.GetMethod(
            "DoDragDrop",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(object), effectsType],
            modifiers: null);
        if (method is null)
        {
            throw new MissingMethodException("Control.DoDragDrop");
        }

        var formType = forms.GetType("System.Windows.Forms.Form", throwOnError: true)!;
        var form = Activator.CreateInstance(formType)!;
        try
        {
            formType.GetProperty("ShowInTaskbar")!.SetValue(form, false);
            formType.GetProperty("Opacity")!.SetValue(form, 0.0);
            formType.GetProperty("FormBorderStyle")!.SetValue(form, Enum.Parse(forms.GetType("System.Windows.Forms.FormBorderStyle")!, "None"));
            formType.GetMethod("Show", Type.EmptyTypes)!.Invoke(form, null);
            method.Invoke(form, [data, effects]);
        }
        finally
        {
            try
            {
                formType.GetMethod("Close", Type.EmptyTypes)!.Invoke(form, null);
                (form as IDisposable)?.Dispose();
            }
            catch
            {
            }
        }

        _ = hwnd;
    }
}
