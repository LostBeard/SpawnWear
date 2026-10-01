using SpawnWear.AppContracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace GenericsProbe
{
    /// <summary>
    /// nanoFramework 2.0 (generics) on-device check. Runs a set of generic constructs on the watch in OnCreate,
    /// logs "[GenericsProbe] PASS|FAIL name" through Debug.WriteLine (visible with tools\nf-attach.cs or
    /// nf-deploy's capture) and draws the tally. Launch it like any SD app: Console `launch GenericsProbe`.
    /// </summary>
    public class GenericsProbeApp : ISpawnApp
    {
        int _pass;
        int _fail;
        bool _dirty = true;

        public string Name => "GENERICS";

        public bool OnCreate(IServiceHost services)
        {
            Run(0, "List<int> add/sum/remove");
            Run(1, "Dictionary<string,int>");
            Run(2, "generic class value+reference");
            Run(3, "generic method Swap<T>");
            Run(4, "nested List<Box<int>>");
            Run(5, "Func<int,int> / Action<string>");
            Run(6, "Nullable<int>");
            Run(7, "Span<byte> slice");
            Run(8, "generic interface IValue<T>");
            Run(9, "List<string>");
            Run(10, "Box<Box<int>>");
            Run(11, "top-level List<TopBox<int>>");
            Run(12, "top-level TopBox<TopBox<int>>");
            Run(13, "top-level interface ITopValue<int>");
            Debug.WriteLine("[GenericsProbe] RESULT " + _pass + "/" + (_pass + _fail) + " passed");
            return true;
        }

        // Each check runs on its own: a preview-runtime fault in one (an exception) is reported as that check's
        // FAIL with the exception type, and the rest still run.
        void Run(int which, string name)
        {
            bool ok;
            string detail = "";
            try
            {
                switch (which)
                {
                    case 0: ok = ListOfInt(); break;
                    case 1: ok = DictionaryOfStringInt(); break;
                    case 2: ok = BoxBoth(); break;
                    case 3: ok = SwapBoth(); break;
                    case 4: ok = NestedGeneric(); break;
                    case 5: ok = Delegates(); break;
                    case 6: ok = NullableInt(); break;
                    case 7: ok = SpanSlice(); break;
                    case 8: ok = GenericInterface(); break;
                    case 9: ok = ListOfString(); break;
                    case 10: ok = BoxOfBox(); break;
                    case 11: ok = TopListOfBox(); break;
                    case 12: ok = TopBoxOfBox(); break;
                    case 13: ok = TopInterface(); break;
                    default: ok = false; break;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                detail = " (" + ex.GetType().Name + ")";
            }
            if (ok) _pass++; else _fail++;
            Debug.WriteLine("[GenericsProbe] " + (ok ? "PASS " : "FAIL ") + name + detail);
        }

        static bool ListOfInt()
        {
            var list = new List<int>();
            for (int i = 1; i <= 5; i++) list.Add(i);
            int sum = 0;
            foreach (int v in list) sum += v;
            list.Remove(3);
            return sum == 15 && list.Count == 4 && list[2] == 4 && list.IndexOf(5) == 3;
        }

        static bool DictionaryOfStringInt()
        {
            var d = new Dictionary<string, int>();
            d["a"] = 1;
            d["b"] = 2;
            d["a"] = 10;
            int b;
            return d.Count == 2 && d["a"] == 10 && d.TryGetValue("b", out b) && b == 2 && !d.ContainsKey("z");
        }

        class Box<T>
        {
            public T Value;
            public Box(T value) { Value = value; }
        }

        static bool BoxBoth()
        {
            var i = new Box<int>(42);
            var s = new Box<string>("watch");
            return i.Value == 42 && s.Value == "watch";
        }

        static void Swap<T>(ref T a, ref T b) { T t = a; a = b; b = t; }

        static bool SwapBoth()
        {
            int x = 1, y = 2;
            Swap(ref x, ref y);
            string p = "p", q = "q";
            Swap(ref p, ref q);
            return x == 2 && y == 1 && p == "q" && q == "p";
        }

        static bool ListOfString()
        {
            var list = new List<string>();
            list.Add("a");
            list.Add("b");
            return list.Count == 2 && list[1] == "b";
        }

        static bool BoxOfBox()
        {
            var inner = new Box<int>(5);
            var outer = new Box<Box<int>>(inner);
            return outer.Value.Value == 5;
        }

        // Same shapes as the nested checks above, with TOP-LEVEL generic types (see bottom of file), to tell a
        // nested-type problem from a generic-instantiation one.
        static bool TopListOfBox()
        {
            var boxes = new List<TopBox<int>>();
            for (int i = 0; i < 3; i++) boxes.Add(new TopBox<int>(i * 10));
            return boxes.Count == 3 && boxes[2].Value == 20;
        }

        static bool TopBoxOfBox()
        {
            var outer = new TopBox<TopBox<int>>(new TopBox<int>(5));
            return outer.Value.Value == 5;
        }

        static bool TopInterface()
        {
            ITopValue<int> v = new TopSeven();
            return v.Get() == 7;
        }

        static bool NestedGeneric()
        {
            var boxes = new List<Box<int>>();
            for (int i = 0; i < 3; i++) boxes.Add(new Box<int>(i * 10));
            return boxes.Count == 3 && boxes[2].Value == 20;
        }

        static bool Delegates()
        {
            Func<int, int> square = v => v * v;
            string seen = null;
            Action<string> record = v => seen = v;
            record("ok");
            return square(7) == 49 && seen == "ok";
        }

        static bool NullableInt()
        {
            int? n = null;
            bool wasNull = !n.HasValue;
            n = 5;
            return wasNull && n.HasValue && n.Value == 5;
        }

        static bool SpanSlice()
        {
            Span<byte> span = new byte[] { 1, 2, 3, 4, 5 };
            Span<byte> mid = span.Slice(1, 3);
            mid[0] = 20;
            return mid.Length == 3 && mid[2] == 4 && span[1] == 20;
        }

        interface IValue<T> { T Get(); }

        class Seven : IValue<int> { public int Get() => 7; }

        static bool GenericInterface()
        {
            IValue<int> v = new Seven();
            return v.Get() == 7;
        }

        public void OnResume(IDisplayBuffer fb) { _dirty = true; Render(fb); }

        public void OnPause() { }

        public void OnDestroy() { }

        public void Tick(IDisplayBuffer fb) { if (_dirty) Render(fb); }

        public bool OnTap(int x, int y) { _dirty = true; return true; }

        void Render(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int top = fb.StatusBarHeight;
            int bottom = fb.PanelHeight - fb.PageIndicatorHeight;
            fb.Clear(Color.FromArgb(20, 20, 30));
            int titleW = fb.MeasureString(Name, 3);
            fb.DrawString(Name, (w - titleW) / 2, top + 30, 3, Color.White);
            string body = _pass + "/" + (_pass + _fail) + (_fail == 0 ? " OK" : " FAIL");
            int bodyW = fb.MeasureString(body, 5);
            fb.DrawString(body, (w - bodyW) / 2, (top + bottom - 35) / 2, 5, _fail == 0 ? Color.LimeGreen : Color.Red);
            fb.Flush();
            _dirty = false;
        }
    }

    public class TopBox<T>
    {
        public T Value;
        public TopBox(T value) { Value = value; }
    }

    public interface ITopValue<T> { T Get(); }

    public class TopSeven : ITopValue<int> { public int Get() => 7; }
}
