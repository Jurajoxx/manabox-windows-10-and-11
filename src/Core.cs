using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManaBoxCompanion
{
    /// <summary>Per-user storage: cache, decks, history and settings live in %LOCALAPPDATA%\ManaBoxCompanion.</summary>
    static class AppData
    {
        public static readonly string Dir;
        static readonly object saveLock = new object();

        static AppData()
        {
            string over = Environment.GetEnvironmentVariable("MANABOX_COMPANION_DATA");
            Dir = string.IsNullOrEmpty(over)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManaBoxCompanion")
                : over;
            Directory.CreateDirectory(Dir);
        }

        public static string PathOf(string name) { return Path.Combine(Dir, name); }

        public static JavaScriptSerializer NewJs()
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js;
        }

        public static T Load<T>(string name) where T : class, new()
        {
            try
            {
                string p = PathOf(name);
                if (File.Exists(p))
                {
                    T v = NewJs().Deserialize<T>(File.ReadAllText(p, Encoding.UTF8));
                    if (v != null) return v;
                }
            }
            catch { }
            return new T();
        }

        public static void Save(string name, object value)
        {
            try
            {
                string json = NewJs().Serialize(value);
                lock (saveLock)
                {
                    string p = PathOf(name);
                    string tmp = p + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(p)) File.Delete(p);
                    File.Move(tmp, p);
                }
            }
            catch { }
        }
    }

    class Settings
    {
        public string LastFile;
        public string Currency = "USD";
        public string View = "gallery";
        public double Width;
        public double Height;
        public bool Maximized;
    }

    static class Fmt
    {
        public static string Currency = "USD";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Sym(string cur) { return cur == "EUR" ? "€" : "$"; }

        public static string Money(double? v) { return Money(v, Currency); }

        public static string Money(double? v, string cur)
        {
            if (v == null) return "—";
            return Sym(cur) + v.Value.ToString("N2", Inv);
        }

        public static string Int(int v) { return v.ToString("N0", Inv); }

        public static double? Num(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            double d;
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float, Inv, out d)) return d;
            if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out d)) return d;
            return null;
        }

        public static string NumText(double v) { return v.ToString("0.##", Inv); }

        /// <summary>near_mint -> Near mint</summary>
        public static string Title(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('_', ' ').Trim();
            if (s.Length == 0) return "";
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    static class Csv
    {
        public static List<string[]> Parse(string text, char delim)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false, any = false;
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        inQuotes = false; i++; continue;
                    }
                    sb.Append(c); i++; continue;
                }
                if (c == '"') { inQuotes = true; any = true; i++; continue; }
                if (c == delim) { row.Add(sb.ToString()); sb.Length = 0; any = true; i++; continue; }
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
                    i++;
                    if (any || sb.Length > 0) { row.Add(sb.ToString()); rows.Add(row.ToArray()); }
                    row.Clear(); sb.Length = 0; any = false;
                    continue;
                }
                sb.Append(c); i++;
            }
            if (any || sb.Length > 0) { row.Add(sb.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }

        public static string Escape(string v, char delim)
        {
            if (v == null) return "";
            if (v.IndexOf(delim) >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        public static string Line(string[] fields, char delim)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(delim);
                sb.Append(Escape(fields[i], delim));
            }
            return sb.ToString();
        }
    }

    static class Ui
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Opens the window at a screen position in pixels (used by scripted runs to stay off the main monitor).</summary>
        public static void PlaceAt(Window w, int x, int y)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = x;
            w.Top = y;
            w.SourceInitialized += delegate
            {
                // NOSIZE | NOZORDER | NOACTIVATE
                SetWindowPos(new WindowInteropHelper(w).Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            };
        }

        public static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        public static object LoadXaml(string name)
        {
            using (Stream s = Resource(name)) return XamlReader.Load(s);
        }

        public static Brush B(string key) { return (Brush)Application.Current.Resources[key]; }

        public static void StyleWindow(Window w)
        {
            w.Background = B("Bg");
            w.Foreground = B("Fg");
            w.FontFamily = new FontFamily("Segoe UI");
            w.FontSize = 13;
            w.UseLayoutRounding = true;
            w.SourceInitialized += delegate
            {
                try
                {
                    IntPtr h = new WindowInteropHelper(w).Handle;
                    int on = 1;
                    if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
                }
                catch { }
            };
            try
            {
                using (Stream s = Resource("app.ico"))
                    if (s != null) w.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }
        }

        public static TextBlock Text(string s, double size, string brush, bool bold)
        {
            var t = new TextBlock();
            t.Text = s;
            t.FontSize = size;
            t.Foreground = B(brush);
            if (bold) t.FontWeight = FontWeights.SemiBold;
            t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        public static Window Dialog(Window owner, string title, double width)
        {
            var w = new Window();
            w.Title = title;
            w.Width = width;
            w.SizeToContent = SizeToContent.Height;
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ResizeMode = ResizeMode.NoResize;
            w.ShowInTaskbar = false;
            StyleWindow(w);
            if (Program.Automated) w.ShowActivated = false;
            if (DialogHook != null)
                w.Loaded += delegate
                {
                    w.Dispatcher.BeginInvoke(new Action(delegate { if (DialogHook != null) DialogHook(w); }),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                };
            return w;
        }

        /// <summary>Set by the self-test to drive dialogs without a person at the keyboard.</summary>
        public static Action<Window> DialogHook;

        /// <summary>Small text prompt. Returns null when cancelled.</summary>
        public static string Prompt(Window owner, string title, string label, string initial)
        {
            Window w = Dialog(owner, title, 400);
            var sp = new StackPanel();
            sp.Margin = new Thickness(18);
            sp.Children.Add(Text(label, 13, "Dim", false));
            var box = new TextBox();
            box.Text = initial ?? "";
            box.Margin = new Thickness(0, 8, 0, 14);
            sp.Children.Add(box);
            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var ok = new Button();
            ok.Content = "OK";
            ok.IsDefault = true;
            ok.MinWidth = 80;
            ok.Style = (Style)Application.Current.Resources["Primary"];
            var cancel = new Button();
            cancel.Content = "Cancel";
            cancel.IsCancel = true;
            cancel.MinWidth = 80;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            sp.Children.Add(buttons);
            w.Content = sp;
            string result = null;
            ok.Click += delegate { result = box.Text.Trim(); w.DialogResult = true; };
            w.Loaded += delegate { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true && !string.IsNullOrEmpty(result) ? result : null;
        }
    }
}
