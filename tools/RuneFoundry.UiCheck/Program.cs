using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RuneFoundry.Core;
using RuneFoundry.Core.Formats;
using RuneFoundry.Editor.Views;
using RuneFoundry.UI;

namespace UiCheck;

/// <summary>
/// Drives the editor's own views and reads the controls back.
///
/// Not simulated input: these are the views the editor builds, attached to a real session
/// and project, so what a control holds afterwards is what a person would see on screen.
///
/// The dispatcher runs on the main thread and is left alone to pump. The checks run on a
/// second thread and reach the UI through Invoke. Pumping the dispatcher from inside a
/// dispatcher callback looks like it should work and does not: an await scheduled back to
/// the UI thread never resumes, and a stuck view is then indistinguishable from a stuck
/// harness.
/// </summary>
internal static class Program
{
    private static int failures;
    private static int checks;
    private static Application app = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var source in new[] { "Icons.xaml", "Theme.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/RuneFoundry.UI;component/{source}"),
            });

        var exit = 0;
        var worker = new Thread(() =>
        {
            try
            {
                exit = Run(args);
            }
            catch (Exception error)
            {
                Console.WriteLine("the harness itself failed: " + error);
                exit = 3;
            }
            finally
            {
                app.Dispatcher.Invoke(() => app.Shutdown());
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        app.Run();
        worker.Join(TimeSpan.FromSeconds(10));
        return exit;
    }

    // ---- talking to the UI thread ------------------------------------------------

    /// <summary>Everything under a control, so a check can look at what was actually drawn.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static T On<T>(Func<T> work) => app.Dispatcher.Invoke(work);
    private static void On(Action work) => app.Dispatcher.Invoke(work);

    /// <summary>Waits for the views to catch up. Safe: this is not the UI thread.</summary>
    private static bool Until(Func<bool> ready, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (On(ready)) return true;
            Thread.Sleep(50);
        }
        return On(ready);
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        checks++;
        var extra = ok || detail.Length == 0 ? "" : "  (" + detail + ")";
        Console.WriteLine($"  {(ok ? "pass" : "FAIL")}  {what}{extra}");
        if (!ok) failures++;
    }

    private static int Run(string[] args)
    {
        var session = On(() =>
        {
            var made = new Session();
            made.AutoDetect();
            return made;
        });

        if (session.Game is null)
        {
            Console.WriteLine("no game install found");
            return 2;
        }

        Console.WriteLine($"game   {session.Game.Root}");

        if (args.Length > 1)
        {
            shots = args[1];
            System.IO.Directory.CreateDirectory(shots);
            Console.WriteLine($"shots  {shots}");
        }

        ModProject? project = null;
        if (args.Length > 0 && System.IO.File.Exists(args[0]))
        {
            project = ModProject.Load(args[0]);
            Console.WriteLine($"mod    {project.Name}");
        }
        else
        {
            Console.WriteLine("mod    none given; checks needing one are skipped");
        }

        SelfTest();
        Sprites(session, project);
        Icons(session, project);
        Scripts(session, project);
        Editor(session, project, args.Length > 0 ? args[0] : null);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"{checks} UI checks, all passed"
            : $"{checks} UI checks, {failures} failed");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Proves the harness can see an await finish. Without it a stuck view and a stuck
    /// harness report the same thing.
    /// </summary>
    private static void SelfTest()
    {
        Console.WriteLine("\nHarness");

        var done = false;
        On(() => _ = app.Dispatcher.InvokeAsync(async () =>
        {
            await System.Threading.Tasks.Task.Run(() => Thread.Sleep(50));
            done = true;
        }));

        Check("an await on the UI thread is seen to finish", Until(() => done, 15));
    }

    // ---- helpers -----------------------------------------------------------------

    private static Window Host(UIElement view) => On(() =>
    {
        var window = new Window
        {
            Content = view,
            Width = 1280,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowActivated = false,
        };
        window.Show();
        return window;
    });

    private static string shots = "";

    /// <summary>
    /// Writes what a view currently looks like to a PNG.
    ///
    /// Some things are only judged by looking: whether a marker is drawn, whether a button
    /// is the one on screen, whether a column is clipped. Rendering the view we already
    /// built costs nothing and makes those reviewable without opening the editor.
    /// </summary>
    private static void Shot(Window window, string name)
    {
        if (shots.Length == 0) return;

        On(() =>
        {
            window.UpdateLayout();

            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);
            if (width <= 0 || height <= 0 || window.Content is not System.Windows.Media.Visual content) return;

            var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width * 2, height * 2, 192, 192,
                System.Windows.Media.PixelFormats.Pbgra32);
            target.Render(content);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

            var path = System.IO.Path.Combine(shots, name + ".png");
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);

            Console.WriteLine($"        wrote {name}.png");
        });
    }

    private static List<object> Items(ItemsControl? control) =>
        (control?.ItemsSource as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();

    private static string? PathOf(object row) =>
        row.GetType().GetProperty("Path")?.GetValue(row) as string;

    // ---- sprites ------------------------------------------------------------------

    private static void Sprites(Session session, ModProject? project)
    {
        Console.WriteLine("\nSprites");

        var view = On(() => new SpritesView());
        On(() => view.Attach(session));
        var window = Host(view);
        On(() => view.Refresh(project));

        var list = On(() => view.FindName("SpriteList") as ListBox);
        var frames = On(() => view.FindName("FrameList") as ListBox);
        var eras = On(() => view.FindName("EraBox") as ComboBox);

        if (list is null || frames is null || eras is null)
        {
            Check("the view has its controls", false, "a control is missing");
            On(window.Close);
            return;
        }

        Until(() => Items(list).Count > 200);
        var rows = On(() => Items(list));
        Check("the sprite list fills", rows.Count > 200,
            $"{rows.Count} rows; {On(() => (view.FindName("Summary") as TextBlock)?.Text)}");

        var rock = rows.FirstOrDefault(r => PathOf(r) == "art/unit/other/rock.grp");
        if (rock is null)
        {
            Check("the runestone is listed", false);
            On(window.Close);
            return;
        }

        On(() => list.SelectedItem = rock);
        Until(() => Items(frames).Count > 0);

        // The runestone is rock_0 plus five facings. Counting alone found one of six.
        Check("QA 11: a sprite shows every frame it has",
            On(() => Items(frames).Count) == 6, $"showed {On(() => Items(frames).Count)}");

        var seasons = On(() => Items(eras).Select(o => o?.ToString() ?? "").ToList());
        Check("QA 12: only the seasons it is drawn in are offered",
            seasons.Count == 2 && seasons.Contains("forest") && seasons.Contains("swamp"),
            "offered " + string.Join(", ", seasons));

        // The note names the other seasons' sprites only when there is a project to change
        // them in; without one it says so instead, which is its own correct answer.
        if (project is not null)
        {
            var note = On(() => (view.FindName("ChosenNote") as TextBlock)?.Text ?? "");
            Check("the note names the sprites that draw the other seasons",
                note.Contains("s_rock", StringComparison.Ordinal), note);
        }

        // A winter-only sprite used to hand its sheet to the next forest one.
        var winter = rows.FirstOrDefault(r => PathOf(r) == "art/unit/other/s_rock.grp");
        if (winter is not null)
        {
            On(() => list.SelectedItem = winter);
            Until(() => Items(frames).Count > 0);
            var winterCount = On(() => Items(frames).Count);

            On(() => list.SelectedItem = rock);
            Until(() => Items(frames).Count == 6);

            Check("QA 16: another season's art is not handed over",
                On(() => Items(frames).Count) == 6 && winterCount > 0,
                $"winter {winterCount}, back to {On(() => Items(frames).Count)}");
        }

        // A unit with many frames, so the list is not only exercised on a six frame case.
        var peon = rows.FirstOrDefault(r => PathOf(r) == "art/unit/orc/peon.grp");
        if (peon is not null)
        {
            On(() => list.SelectedItem = peon);
            Until(() => Items(frames).Count > 10);
            Check("a unit with a long animation still lists its frames",
                On(() => Items(frames).Count) > 10, $"{On(() => Items(frames).Count)} frames");
        }

        On(() => list.SelectedItem = rock);
        Until(() => Items(frames).Count == 6);
        Shot(window, "sprites");

        On(window.Close);
    }

    // ---- icons --------------------------------------------------------------------

    private static void Icons(Session session, ModProject? project)
    {
        Console.WriteLine("\nIcons");

        var view = On(() => new IconsView());
        On(() => view.Attach(session));
        var window = Host(view);
        On(() => view.Refresh(project));

        var list = On(() => view.FindName("IconList") as ListBox);
        var picker = On(() => view.FindName("SourceBox") as ComboBox);

        if (list is null || picker is null)
        {
            Check("the view has its controls", false, "a control is missing");
            On(window.Close);
            return;
        }

        Until(() => Items(list).Count > 100);
        var icons = On(() => Items(list));
        Check("the icon list fills", icons.Count > 100, $"{icons.Count} icons");

        if (icons.Count > 10)
        {
            On(() => list.SelectedItem = icons[10]);
            Until(() => Items(picker).Count > 0, 20);

            var offered = On(() => Items(picker).Count);
            Check("QA 18: the art picker has items in it", offered > 100,
                $"the dropdown holds {offered}");

            Check("QA 18: the picker is usable with a mod open",
                project is null || On(() => picker.IsEnabled), "it is disabled");

            Shot(window, "icons");
        }

        On(window.Close);
    }

    // ---- AI scripts ---------------------------------------------------------------

    private static void Scripts(Session session, ModProject? project)
    {
        Console.WriteLine("\nAI scripts");

        // The wizard is a window of its own, so it is shown off screen rather than
        // re-parented: taking its content out from under it changes what is being tested.
        var wizard = On(() =>
        {
            var made = new WaveWizard(roomBytes: 200, buildsAlready: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowActivated = false,
            };
            made.Show();
            return made;
        });

        var ways = On(() => Items(wizard.FindName("DomainList") as ItemsControl).Count);
        Check("the wizard offers all three ways to attack", ways == 3, $"{ways} offered");

        var preview = On(() => (wizard.FindName("Preview") as TextBlock)?.Text ?? "");
        Check("the wizard says what it will write",
            preview.Contains("attacks by land", StringComparison.Ordinal), preview);

        var room = On(() => (wizard.FindName("Room") as TextBlock)?.Text ?? "");
        Check("the wizard says whether the wave fits",
            room.Contains("fits", StringComparison.Ordinal), room);

        // Asking for a bigger party than the wave builds is the one way to write a wave
        // that stops the script for good, so the warning has to appear.
        var starved = On(() =>
        {
            if (wizard.FindName("DomainList") is not ItemsControl list) return "";
            if (Items(list).FirstOrDefault() is not { } land) return "";

            var size = land.GetType().GetProperty("SizeIndex");
            size?.SetValue(land, 7);      // the largest party the wizard offers
            return (wizard.FindName("Room") as TextBlock)?.Text ?? "";
        });

        Check("the wizard warns when a wave gathers more than it builds",
            starved.Contains("never ends", StringComparison.Ordinal), starved);

        On(wizard.Close);

        // Pasting a script in: the window has to refuse what the save would refuse, and it
        // has to say so before the button can be pressed rather than after.
        var stock = session.Game is null
            ? null
            : Path.Combine(session.Game.DataRoot, "Rez", "ai.bin");

        if (stock is not null && File.Exists(stock))
        {
            var file = AiFile.Parse(File.ReadAllBytes(stock));
            var script = file.Scripts.First(s => !s.IsEmpty && s.Instructions.Count > 4);
            var slot = script.CodeCapacity - AiScript.HeaderLength;
            var text = AiFile.ToText(script);

            var paste = On(() =>
            {
                var made = new ScriptText(script.Name, slot, text)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowActivated = false,
                };
                made.Show();
                return made;
            });

            var accepts = On(() => (paste.FindName("Accept") as Button)?.IsEnabled == true);
            var verdict = On(() => (paste.FindName("Verdict") as TextBlock)?.Text ?? "");

            Check("a script pasted back into its own slot is accepted", accepts, verdict);
            Check("and the window says what it weighs",
                verdict.Contains($"of {slot} bytes", StringComparison.Ordinal), verdict);

            var refused = On(() =>
            {
                if (paste.FindName("Source") is not TextBox box) return ("", true);

                box.Text = text + "\n" + text;
                return ((paste.FindName("Verdict") as TextBlock)?.Text ?? "",
                        (paste.FindName("Accept") as Button)?.IsEnabled == true);
            });

            Check("a script too big for the slot is refused",
                !refused.Item2 && refused.Item1.Contains("holds", StringComparison.Ordinal),
                refused.Item1);

            var broken = On(() =>
            {
                if (paste.FindName("Source") is not TextBox box) return ("", true);

                box.Text = "this is not an AI script";
                return ((paste.FindName("Verdict") as TextBlock)?.Text ?? "",
                        (paste.FindName("Accept") as Button)?.IsEnabled == true);
            });

            Check("and so is text that is not a script at all", !broken.Item2, broken.Item1);

            On(paste.Close);
        }
    }

    // ---- the editor window --------------------------------------------------------

    private static void Editor(Session session, ModProject? project, string? path)
    {
        Console.WriteLine("\nEditor");

        if (path is null)
        {
            Console.WriteLine("  (no mod given, skipped)");
            return;
        }

        var view = On(() => new EditorView(session));
        var window = Host(view);
        On(() => view.EnsureInitialised());
        On(() => view.OpenRecent(path));

        // The names of the sides, which live in the language file under keys nobody would
        // guess, are on the mod details page.
        var human = On(() => Items(view.FindName("HumanClanList") as ItemsControl).Count);
        var orc = On(() => Items(view.FindName("OrcClanList") as ItemsControl).Count);
        var expansion = On(() => Items(view.FindName("ExpansionClanList") as ItemsControl).Count);

        Check("the sides are listed on the mod details page",
            human == 8 && orc == 8 && expansion == 7,
            $"human {human}, orc {orc}, expansion {expansion}");

        var named = On(() =>
        {
            var rows = Items(view.FindName("HumanClanList") as ItemsControl);
            return rows.Select(r => r.GetType().GetProperty("Name")?.GetValue(r) as string ?? "")
                       .ToList();
        });

        Check("each side carries the name the game gives it",
            named.Any(n => n.Contains("Lordaeron", StringComparison.Ordinal)),
            "the human list held: " + string.Join(", ", named.Take(3)));

        var launch = On(() => view.FindName("LaunchTargetBox") as ComboBox);
        var launches = On(() => Items(launch));

        Check("the launch list starts at the game's own front end",
            launches.Count > 50 && launches[0]?.ToString() == "Main menu",
            $"{launches.Count} entries, first is {launches.FirstOrDefault()}");

        Check("the launch list names a mission the way you would type it",
            launches.Any(o => o?.ToString()?.Contains("(orc01)", StringComparison.Ordinal) == true),
            "no entry mentions orc01");

        // The units tab. A unit's numbers live in the .dat table and its name lives in the
        // language file, so renaming one leaves the table untouched — and the mark that
        // says "this mod changed this" was only ever read off the table.
        On(() =>
        {
            if (view.FindName("UnitsLens") is System.Windows.Controls.Primitives.ToggleButton lens)
                lens.IsChecked = true;
        });

        Until(() => Items(view.FindName("DatRecordList") as ListBox).Count > 0, 30);

        var units = On(() => view.FindName("DatRecordList") as ListBox);
        var title = On(() => view.FindName("DatRecordTitle") as TextBox);

        if (units is null || title is null)
        {
            Check("the units tab has its list", false, "DatRecordList or DatRecordTitle is missing");
        }
        else
        {
            var unitRows = On(() => Items(units));
            Check("the units are listed", unitRows.Count > 100, $"{unitRows.Count} unitRows");

            // A unit nobody has touched, so the mark this writes is this check's own.
            var clean = unitRows.FirstOrDefault(r =>
                r.GetType().GetProperty("Edited")?.GetValue(r) as bool? == false);

            if (clean is null)
            {
                Check("a unit with no changes was found to rename", false, "every unit is already marked");
            }
            else
            {
                var index = (int)clean.GetType().GetProperty("Index")!.GetValue(clean)!;
                var was = On(() =>
                {
                    units.SelectedItem = clean;
                    return title.Text;
                });

                On(() =>
                {
                    title.Text = was + " of the Watch";

                    // The editor commits a name when the box loses focus or the selection
                    // moves. Off-screen there is no focus to lose, so the selection is what
                    // this uses — the same path a person takes moving to the next unit.
                    units.SelectedItem = unitRows.First(r => !ReferenceEquals(r, clean));
                    units.SelectedItem = clean;
                });

                var marked = On(() => Items(units)
                    .Any(r => (int?)r.GetType().GetProperty("Index")?.GetValue(r) == index
                              && r.GetType().GetProperty("Edited")?.GetValue(r) as bool? == true));

                Check("renaming a unit marks it, even with its numbers untouched", marked,
                    $"{was} was renamed and stayed unmarked");

                // Put it back the way it was found.
                // Undo is also how this check cleans up: the name it typed goes back.
                var undone = On(() =>
                {
                    var edit = session.Undo.Undo();
                    units.SelectedItem = clean;
                    return edit?.Describe ?? "";
                });

                Check("the rename is on the undo stack under the name it had",
                    undone.StartsWith(was + " renamed to", StringComparison.Ordinal),
                    undone.Length == 0 ? "nothing was on the stack" : undone);

                Check("and the box shows the old name again", On(() => title.Text) == was,
                    On(() => title.Text));

                var back = On(() => Items(units)
                    .Any(r => (int?)r.GetType().GetProperty("Index")?.GetValue(r) == index
                              && r.GetType().GetProperty("Edited")?.GetValue(r) as bool? == false));

                Check("and undoing the rename takes the mark away", back,
                    "the mark outlived the rename");
            }
        }

        // A unit's lines: the buttons that act on one have to be on the row, not only in a
        // menu nobody thinks to open.
        On(() =>
        {
            var list = view.FindName("DatRecordList") as ListBox;
            var footman = Items(list!).FirstOrDefault(r =>
                (int?)r.GetType().GetProperty("Index")?.GetValue(r) == 0);

            if (footman is not null) list!.SelectedItem = footman;
        });

        // The rows are generated after the selection settles, so give them a moment.
        Until(() => Descendants(view.FindName("DatSoundList") as DependencyObject)
            .OfType<Button>().Any(b => b.Content is string), 20);

        var sounds = On(() =>
        {
            return Descendants(view.FindName("DatSoundList") as DependencyObject)
                .OfType<Button>()
                .Select(b => b.Content as string ?? "")
                .Where(c => c.Length > 0)
                .Distinct()
                .ToList();
        });

        Check("a unit's sound offers silence on the row",
            sounds.Contains("Silence"), string.Join(", ", sounds));

        Check("and a way back from it",
            sounds.Contains("Reset"), string.Join(", ", sounds));

        On(() =>
        {
            var list = view.FindName("DatRecordList") as ListBox;
            var row = Items(list!).FirstOrDefault(r =>
                (int?)r.GetType().GetProperty("Index")?.GetValue(r) == 0x2C);
            if (row is not null) list!.SelectedItem = row;
        });

        Until(() => Descendants(view.FindName("DatSoundList") as DependencyObject)
            .OfType<Button>().Any(b => b.Content is string), 20);

        // A hero has no description string, and the sounds used to be nested inside the
        // description panel — so hiding one hid the other, and every hero's lines were on
        // screen for nobody.
        var hero = On(() =>
        {
            var text = view.FindName("DatTextPanel") as FrameworkElement;
            var panel = view.FindName("DatSoundPanel") as FrameworkElement;
            var rows = Items(view.FindName("DatSoundList") as ItemsControl).Count;

            return (Text: text?.Visibility, Panel: panel?.Visibility,
                    Rows: rows, Drawn: panel?.ActualHeight ?? 0);
        });

        Check("a unit with no description still shows its sounds",
            hero.Rows > 0 && hero.Panel == Visibility.Visible && hero.Drawn > 0,
            $"{hero.Rows} rows, panel {hero.Panel}, description {hero.Text}, {hero.Drawn:0}px tall");

        // One column, one bar. The words and the numbers were two scrollers stacked inside
        // one pane, and a pane with two bars down its edge is a pane where neither one
        // obviously moves what you meant.
        var bars = On(() => Descendants(view.FindName("DatFieldList") as DependencyObject)
            .OfType<System.Windows.Controls.Primitives.ScrollBar>()
            .Count(b => b.IsVisible && b.Orientation == Orientation.Vertical)
            + Descendants(view.FindName("DatSoundPanel") as DependencyObject)
                .OfType<System.Windows.Controls.Primitives.ScrollBar>()
                .Count(b => b.IsVisible && b.Orientation == Orientation.Vertical));

        Check("the record pane scrolls in one place", bars == 0,
            $"{bars} scrollbars inside the record's own content");

        Shot(window, "units-turalyon");

        // The AI tab: pick it the way the strip does, then wait for the scripts.
        On(() =>
        {
            if (view.FindName("AiLens") is System.Windows.Controls.Primitives.ToggleButton lens)
                lens.IsChecked = true;
        });

        // A script that fills its slot has nowhere to grow, so the way out of that has to
        // be on screen rather than in a comment about why it is not offered.
        var room = On(() =>
        {
            var button = view.FindName("AiMakeRoomButton") as Button;
            return (Found: button is not null, Enabled: button?.IsEnabled == true);
        });

        Check("the AI tab offers a way to make room", room.Found && room.Enabled,
            room.Found ? "it is there but disabled" : "AiMakeRoomButton is missing");

        var scripts = On(() => view.FindName("AiScriptList") as ListBox);
        var blocks = On(() => view.FindName("AiBlockList") as ListBox);

        if (scripts is null || blocks is null)
        {
            Check("the AI tab has its controls", false, "a control is missing");
            On(window.Close);
            return;
        }

        Until(() => Items(scripts).Count > 80, 40);
        var rows = On(() => Items(scripts));
        Check("all eighty four AI scripts are listed", rows.Count >= 84, $"{rows.Count} listed");

        var green = Named(rows, "Orc 14 (Green)");
        if (green is not null)
        {
            On(() => scripts.SelectedItem = green);
            Until(() => Items(blocks).Count > 10, 30);

            var said = On(() => Items(blocks)
                .Select(b => b.GetType().GetProperty("Summary")?.GetValue(b) as string ?? "")
                .ToList());

            // A build limit shows its wording in the picker beside the row, not in the
            // summary column, so that is where to read it.
            var picked = On(() => Items(blocks)
                .Select(b => b.GetType().GetProperty("ValueChoice")?.GetValue(b))
                .Where(c => c is not null)
                .Select(c => c!.GetType().GetProperty("Name")?.GetValue(c) as string ?? "")
                .ToList());

            Check("QA 1: a build limit names where building stops",
                picked.Any(s => s.Contains("down to", StringComparison.Ordinal)),
                "the pickers held: " + string.Join(" | ", picked.Distinct().Take(4)));

            Check("QA 1: a build limit offers a stopping point, not a position",
                !picked.Any(s => s.Length > 0 && int.TryParse(s, out _)),
                "a picker offered a bare number");

            Check("QA 1: no row falls back to a number",
                !said.Any(s => s.Contains("build-list entry", StringComparison.Ordinal)
                            || s.Contains("Variable $", StringComparison.Ordinal)),
                "a row still shows a raw number");

            // The opening block is 24 to 26 settings, never the whole script.
            var sections = On(() => Items(blocks)
                .Select(b => b.GetType().GetProperty("Section")?.GetValue(b) as string ?? "")
                .ToList());

            var setup = sections.Count(s => s == "SETUP");
            Check("QA 4: the opening block stops where the settings stop",
                setup is >= 20 and <= 26, $"{setup} rows in the block");

            Check("the rest of the script divides into waves",
                sections.Any(s => s.StartsWith("WAVE ", StringComparison.Ordinal)),
                "no wave was found");
        }

        var orc4 = Named(rows, "Orc 4");
        if (orc4 is not null)
        {
            On(() => scripts.SelectedItem = orc4);
            Until(() => Items(blocks).Count > 0, 30);

            var said = On(() => Items(blocks)
                .Select(b => b.GetType().GetProperty("Summary")?.GetValue(b) as string ?? "")
                .ToList());

            Check("QA 2: a script with no build list says so",
                said.Any(s => s.Contains("no build list", StringComparison.Ordinal))
                || !said.Any(s => s.Contains("everything in the build list", StringComparison.Ordinal)),
                "it claimed to build something");
        }

        if (green is not null)
        {
            On(() => scripts.SelectedItem = green);
            Until(() => Items(blocks).Count > 10, 30);

            // The line under the editor lists what may be typed. It went on naming three
            // opcodes after they were removed, which no assertion caught and a picture did.
            var hint = On(() => (view.FindName("AiHint") as TextBlock)?.Text ?? "");
            Check("the source hint names only the instructions that exist",
                hint.Length > 0
                && !hint.Contains(" do ", StringComparison.Ordinal)
                && !hint.Contains("rate", StringComparison.Ordinal)
                && !hint.Contains("item", StringComparison.Ordinal),
                hint);

            Shot(window, "ai-scripts");
        }

        Campaign(session, project);
        On(window.Close);
    }

    // ---- campaign ------------------------------------------------------------------

    private static void Campaign(Session session, ModProject? project)
    {
        Console.WriteLine("\nCampaign");

        var view = On(() => new CampaignView());
        On(() => view.Attach(session));
        var window = Host(view);
        On(() => view.Refresh(project));

        var list = On(() => view.FindName("MissionList") as ListBox);
        if (list is null)
        {
            Check("the campaign view has its list", false, "MissionList is missing");
            On(window.Close);
            return;
        }

        Until(() => Items(list).Count > 20, 40);
        var rows = On(() => Items(list));
        Check("the campaigns and their missions are listed", rows.Count > 20,
            $"{rows.Count} rows");

        // A mission the mod changes carries the same mark the file tree draws.
        var marks = On(() => Items(list)
            .Select(r => r.GetType().GetProperty("Marker")?.GetValue(r) as string ?? "")
            .ToList());

        Check("QA 20: a changed mission is marked the way a changed file is",
            marks.All(m => m.Length == 0 || m == "●"),
            "a mark was something else: " + string.Join(" ", marks.Where(m => m.Length > 0).Distinct()));

        // Only one of the two map buttons is ever the right one to press.
        var mission = rows.FirstOrDefault(r =>
            r.GetType().GetProperty("IsMission")?.GetValue(r) as bool? == true);

        if (mission is not null)
        {
            On(() => list.SelectedItem = mission);
            Thread.Sleep(400);

            var open = On(() => view.FindName("MapOpenButton") as Button);
            var copy = On(() => view.FindName("MapCopyButton") as Button);

            if (open is not null && copy is not null)
            {
                var openShown = On(() => open.Visibility == Visibility.Visible);
                var copyShown = On(() => copy.Visibility == Visibility.Visible);

                Check("QA 21: exactly one map button is offered",
                    openShown != copyShown,
                    $"open {openShown}, copy {copyShown}");
            }

            Shot(window, "campaign");
        }

        // The artwork belongs to the campaign, not to a mission, so it needs a campaign row.
        var heading = rows.FirstOrDefault(r =>
            r.GetType().GetProperty("IsCampaign")?.GetValue(r) as bool? == true);

        if (heading is not null)
        {
            On(() => list.SelectedItem = heading);
            Until(() => Items(view.FindName("CampaignArtList") as ItemsControl).Count > 0, 60);

            var art = On(() => Items(view.FindName("CampaignArtList") as ItemsControl));
            Check("the campaign artwork is listed", art.Count > 0, $"{art.Count} rows");

            // The campaign picture is a pair: one at rest, one as the pointer rests on it.
            var paired = On(() =>
            {
                var first = Items(view.FindName("CampaignArtList") as ItemsControl).FirstOrDefault();
                if (first?.GetType().GetProperty("Slots")?.GetValue(first) is not IEnumerable slots)
                    return false;

                return slots.Cast<object>().Any(s =>
                    s.GetType().GetProperty("HasPair")?.GetValue(s) as bool? == true);
            });

            Check("the campaign picture shows both of its states", paired,
                "no slot carried a second picture");

            var drawn = On(() =>
            {
                var pictures = Descendants(view.FindName("CampaignArtList") as DependencyObject)
                    .OfType<Image>()
                    .Where(i => i.Source is not null && i.ActualWidth > 0)
                    .ToList();

                var card = 110.0;
                var spilling = pictures.Count(i => i.ActualHeight > card + 1);

                return (pictures.Count, spilling,
                    Tallest: pictures.Count == 0 ? 0 : pictures.Max(i => i.ActualHeight));
            });

            Check("every campaign picture is drawn inside its card",
                drawn.Count > 0 && drawn.spilling == 0,
                $"{drawn.Count} pictures, {drawn.spilling} spilling, tallest {drawn.Tallest:0}px");

            Shot(window, "campaign-art");

            // Silencing is the mod's own change, and the page said the game's recording was
            // still playing. This drives the real button, then puts the file back, so the
            // project it runs against is left as it was found.
            var closing = On(() => Items(view.FindName("EpilogueList") as ItemsControl));

            if (project is not null && closing.Count > 0)
            {
                var page = closing[0];
                var speech = page.GetType().GetProperty("SpeechPath")?.GetValue(page) as string;

                // A page the mod has already silenced is left alone: this check must not
                // undo somebody's work to prove a point.
                if (speech is not null && !project.HasOverride(speech))
                {
                    var quiet = On(() => Descendants(view.FindName("EpilogueList") as DependencyObject)
                        .OfType<Button>()
                        .FirstOrDefault(b => ReferenceEquals(b.DataContext, page)
                                             && b.Content as string == "Silence"));

                    if (quiet is null)
                    {
                        Check("a closing page offers silence", false, "no Silence button on it");
                    }
                    else
                    {
                        On(() => { quiet.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true; });

                        var state = On(() => page.GetType().GetProperty("SpeechState")?.GetValue(page) as string);
                        Check("a silenced page says the mod owns the recording",
                            state == "replaced by this mod", state ?? "it said nothing");

                        var marked = On(() => heading.GetType().GetProperty("IsChanged")?.GetValue(heading)
                                              as bool? == true);
                        Check("and the campaign carries the mark for it", marked,
                            "the campaign row stayed unmarked");

                        // Undo is also how this check cleans up after itself: if it does not
                        // work, the file it wrote stays, and the next run says so.
                        On(() => session.Undo.Undo());

                        var back = On(() => page.GetType().GetProperty("SpeechState")?.GetValue(page) as string);

                        Check("undo puts a silenced page back",
                            back == "the game's own recording" && !project.HasOverride(speech),
                            $"{back}, {(project.HasOverride(speech) ? "file still there" : "file gone")}");

                        if (project.HasOverride(speech)) project.RemoveOverride(speech);
                        On(() => { view.Refresh(project); return true; });
                    }
                }
            }
        }

        On(window.Close);
    }

    /// <summary>
    /// Finds a row by the name it shows. The rows carry it under different property names,
    /// so both are tried, and a row that cannot be found is reported rather than skipped:
    /// a check that quietly does not run is worse than one that fails.
    /// </summary>
    private static object? Named(List<object> rows, string name)
    {
        var found = rows.FirstOrDefault(r =>
            (r.GetType().GetProperty("Title")?.GetValue(r) as string) == name
            || (r.GetType().GetProperty("Name")?.GetValue(r) as string) == name);

        if (found is null) Check($"the list holds \"{name}\"", false, "it was not there");
        return found;
    }
}
