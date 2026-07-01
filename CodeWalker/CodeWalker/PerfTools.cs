using CodeWalker.Properties;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodeWalker
{
    // Epic RPF: lets the user throw more RAM / VRAM / CPU at CodeWalker so the world
    // (and the MLO gallery) loads from further away and runs smoother. All applied live;
    // cache sizes also persist (re-applied at the next launch via Settings).
    public partial class WorldForm
    {
        private const long GB = 1024L * 1024L * 1024L;

        // Apply the saved cache settings to the live caches (called once on startup).
        private void ApplyPerformanceSettings()
        {
            try
            {
                GameFileCache?.SetCacheSize(Settings.Default.CacheSize);
                Renderer?.RenderableCache?.SetCacheLimits(
                    Settings.Default.GPUGeometryCacheSize,
                    Settings.Default.GPUTextureCacheSize,
                    0);
            }
            catch { }
        }

        private void PerfSettings_Click(object sender, EventArgs e)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Performance / loading";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ClientSize = new Size(430, 250);

                var info = new Label
                {
                    Text = "Higher values = the world loads from further away and stays loaded,\n" +
                           "so flying around is smoother. Costs more RAM / video memory / CPU.\n" +
                           "Applied immediately.",
                    Location = new Point(12, 10),
                    Size = new Size(406, 50)
                };

                NumericUpDown MakeNum(int y, string caption, decimal min, decimal max, decimal val, int dec)
                {
                    var lbl = new Label { Text = caption, Location = new Point(12, y + 3), Size = new Size(250, 20) };
                    var num = new NumericUpDown
                    {
                        Location = new Point(300, y),
                        Size = new Size(110, 24),
                        Minimum = min,
                        Maximum = max,
                        DecimalPlaces = dec,
                        Increment = dec > 0 ? 0.25m : 1m,
                        Value = Math.Min(Math.Max(val, min), max)
                    };
                    dlg.Controls.Add(lbl);
                    dlg.Controls.Add(num);
                    return num;
                }

                int curSpeed = Renderer?.RenderableCache?.MaxItemsPerLoop ?? 8;
                decimal ramGB = Math.Round((decimal)Settings.Default.CacheSize / GB, 2);
                decimal geomGB = Math.Round((decimal)Settings.Default.GPUGeometryCacheSize / GB, 2);
                decimal texGB = Math.Round((decimal)Settings.Default.GPUTextureCacheSize / GB, 2);

                var numSpeed = MakeNum(70, "Loading speed (items per loop, CPU)", 1, 128, curSpeed, 0);
                var numRam = MakeNum(102, "RAM file cache (GB)", 1, 64, ramGB, 2);
                var numGeom = MakeNum(134, "GPU geometry cache (GB)", 0.25m, 16, geomGB, 2);
                var numTex = MakeNum(166, "GPU texture cache (GB)", 0.25m, 16, texGB, 2);

                var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, Location = new Point(244, 208), Size = new Size(80, 28) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(330, 208), Size = new Size(80, 28) };

                dlg.Controls.Add(info);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    int speed = (int)numSpeed.Value;
                    long ramBytes = (long)(numRam.Value * GB);
                    long geomBytes = (long)(numGeom.Value * GB);
                    long texBytes = (long)(numTex.Value * GB);

                    // apply live
                    if (Renderer?.RenderableCache != null)
                    {
                        Renderer.RenderableCache.MaxItemsPerLoop = speed;
                        Renderer.RenderableCache.SetCacheLimits(geomBytes, texBytes, 0);
                    }
                    if (GameFileCache != null)
                    {
                        GameFileCache.MaxItemsPerLoop = speed;
                        GameFileCache.SetCacheSize(ramBytes);
                    }

                    // persist cache sizes (re-applied at construction on next launch)
                    Settings.Default.CacheSize = ramBytes;
                    Settings.Default.GPUGeometryCacheSize = geomBytes;
                    Settings.Default.GPUTextureCacheSize = texBytes;
                    Settings.Default.Save();

                    UpdateStatus("Performance settings applied: " + speed + " items/loop, " +
                                 numRam.Value + " GB RAM, " + numGeom.Value + "+" + numTex.Value + " GB GPU cache.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't apply performance settings:\n" + ex.Message, "Performance");
                }
            }
        }
    }
}
