using CodeWalker.GameFiles;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CodeWalker
{
    // Epic RPF custom additions to the CodeWalker world editor.
    // Adds two Tools-menu commands that operate on the MLO (interior) that the
    // currently-selected object belongs to:
    //   * Select MLO Objects    - selects every entity of that MLO in the viewport.
    //   * Export MLO Objects...  - extracts the model/texture files of every entity
    //                              of that MLO into one folder (+ a manifest), so an
    //                              MLO can be deleted and rebuilt from its parts.
    public partial class WorldForm
    {
        // Called once from the WorldForm constructor.
        private void InitMloTools()
        {
            try
            {
                var selItem = new ToolStripMenuItem("Select MLO Objects");
                selItem.Click += MloToolsSelect_Click;

                var expItem = new ToolStripMenuItem("Export MLO Objects...");
                expItem.Click += MloToolsExport_Click;

                mloGalleryMenuItem = new ToolStripMenuItem("Load All MLOs (void gallery)");
                mloGalleryMenuItem.Click += MloToolsLoadAllMlos_Click;

                var perfItem = new ToolStripMenuItem("Performance / loading (RAM, GPU, speed)...");
                perfItem.Click += PerfSettings_Click;

                ToolsMenu.Items.Add(new ToolStripSeparator());
                ToolsMenu.Items.Add(selItem);
                ToolsMenu.Items.Add(expItem);
                ToolsMenu.Items.Add(mloGalleryMenuItem);
                ToolsMenu.Items.Add(new ToolStripSeparator());
                ToolsMenu.Items.Add(perfItem);

                ApplyPerformanceSettings(); //apply saved perf prefs on startup
            }
            catch
            { } //never let the custom menu break startup
        }


        // Resolve the MLO instance the current selection belongs to (works whether an
        // interior prop is selected, or the MLO entity itself).
        private MloInstanceData GetSelectedMloInstance()
        {
            var sel = SelectedItem;
            if (sel.MultipleSelectionItems != null)
            {
                foreach (var it in sel.MultipleSelectionItems)
                {
                    var inst = GetMloInstanceFromEntity(it.EntityDef);
                    if (inst != null) return inst;
                }
            }
            return GetMloInstanceFromEntity(sel.EntityDef);
        }

        private static MloInstanceData GetMloInstanceFromEntity(YmapEntityDef ent)
        {
            if (ent == null) return null;
            if (ent.MloParent?.MloInstance != null) return ent.MloParent.MloInstance; //an interior prop
            if (ent.MloInstance != null) return ent.MloInstance;                       //the MLO entity itself
            return null;
        }

        private static List<YmapEntityDef> GetMloEntities(MloInstanceData inst)
        {
            var list = new List<YmapEntityDef>();
            if (inst == null) return list;
            if (inst.Entities != null) list.AddRange(inst.Entities);
            if (inst.EntitySets != null)
            {
                foreach (var es in inst.EntitySets)
                {
                    if (es?.Entities != null) list.AddRange(es.Entities);
                }
            }
            return list;
        }


        private void MloToolsSelect_Click(object sender, EventArgs e)
        {
            var inst = GetSelectedMloInstance();
            if (inst == null)
            {
                MessageBox.Show(this, "Select an object that belongs to an MLO (interior) first.", "Select MLO Objects");
                return;
            }

            var ents = GetMloEntities(inst);
            if (ents.Count == 0)
            {
                MessageBox.Show(this, "This MLO has no interior entities to select.", "Select MLO Objects");
                return;
            }

            var items = new List<MapSelection>();
            foreach (var ent in ents)
            {
                items.Add(MapSelection.FromProjectObject(this, ent));
            }
            SelectMulti(items.ToArray());
            UpdateStatus(ents.Count + " MLO object(s) selected.");
        }


        private void MloToolsExport_Click(object sender, EventArgs e)
        {
            var inst = GetSelectedMloInstance();
            if (inst == null)
            {
                MessageBox.Show(this, "Select an object that belongs to an MLO (interior) first.", "Export MLO Objects");
                return;
            }

            var ents = GetMloEntities(inst);
            if (ents.Count == 0)
            {
                MessageBox.Show(this, "This MLO has no interior entities to export.", "Export MLO Objects");
                return;
            }

            string mloName = inst.Owner?.Archetype?.Name
                ?? inst.Owner?._CEntityDef.archetypeName.ToString()
                ?? "mlo";

            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Choose a folder to export the MLO objects into";
                if (fbd.ShowDialog(this) != DialogResult.OK) return;

                var outDir = Path.Combine(fbd.SelectedPath, CleanFileName(mloName) + "_export");

                int ok = 0, fail = 0;
                int missing = 0;
                var entries = new Dictionary<string, RpfFileEntry>(StringComparer.OrdinalIgnoreCase);
                var manifest = new StringBuilder();

                try
                {
                    Cursor = Cursors.WaitCursor;
                    Directory.CreateDirectory(outDir);

                    manifest.AppendLine("# MLO export: " + mloName);
                    manifest.AppendLine("# entities: " + ents.Count);
                    manifest.AppendLine("# columns: archetype \t drawable_file \t texture_file \t local_position \t local_rotation \t scale");

                    // Resolve each entity's model + texture files (unique), and build a manifest row.
                    foreach (var ent in ents)
                    {
                        var hash = ent._CEntityDef.archetypeName;
                        var arch = ent.Archetype ?? GameFileCache.GetArchetype(hash);

                        string drawFile = "(none)", texFile = "";
                        if (arch != null)
                        {
                            RpfFileEntry de = null;
                            if (arch.DrawableDict != 0) de = GameFileCache.GetYddEntry(arch.DrawableDict);
                            if (de == null) de = GameFileCache.GetYdrEntry(arch.Hash);
                            if (de == null) de = GameFileCache.GetYftEntry(arch.Hash);
                            if (de != null) { entries[de.Path] = de; drawFile = de.Name; }
                            else { missing++; }

                            if (arch.TextureDict != 0)
                            {
                                var te = GameFileCache.GetYtdEntry(arch.TextureDict);
                                if (te != null) { entries[te.Path] = te; texFile = te.Name; }
                            }
                        }
                        else
                        {
                            missing++;
                            drawFile = "(archetype not found)";
                        }

                        manifest.Append(hash.ToString()).Append('\t')
                                .Append(drawFile).Append('\t')
                                .Append(texFile).Append('\t')
                                .Append(FmtVec(ent.MloRefPosition)).Append('\t')
                                .Append(FmtQuat(ent.MloRefOrientation)).Append('\t')
                                .Append(FmtVec(ent.Scale)).AppendLine();
                    }

                    // Extract every unique file into the output folder.
                    foreach (var kvp in entries)
                    {
                        var entry = kvp.Value;
                        try
                        {
                            var data = entry.File.ExtractFile(entry);
                            if (data != null)
                            {
                                // Resource files (ydr/ydd/yft/ytd) are stored INSIDE the rpf
                                // without their RSC7 header and uncompressed-on-extract. To make
                                // a valid standalone file that Epic RPF / OpenIV / CodeWalker can
                                // open, recompress the data and prepend the RSC7 header (this is
                                // exactly what CodeWalker's own "Extract" does).
                                if (entry is RpfResourceFileEntry rrfe)
                                {
                                    data = ResourceBuilder.Compress(data);
                                    data = ResourceBuilder.AddResourceHeader(rrfe, data);
                                }
                                File.WriteAllBytes(Path.Combine(outDir, entry.Name), data);
                                ok++;
                            }
                            else fail++;
                        }
                        catch
                        {
                            fail++;
                        }
                    }

                    File.WriteAllText(Path.Combine(outDir, "_manifest.txt"), manifest.ToString());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Export failed:\n" + ex.Message, "Export MLO Objects");
                    return;
                }
                finally
                {
                    Cursor = Cursors.Default;
                }

                var msg = new StringBuilder();
                msg.AppendLine("Exported " + ok + " file(s) to:");
                msg.AppendLine(outDir);
                msg.AppendLine();
                msg.AppendLine(ents.Count + " entities, " + entries.Count + " unique files.");
                if (fail > 0) msg.AppendLine(fail + " file(s) failed to extract.");
                if (missing > 0) msg.AppendLine(missing + " archetype(s) had no resolvable model file.");
                msg.AppendLine();
                msg.AppendLine("A _manifest.txt with archetype names + local transforms was written too.");
                MessageBox.Show(this, msg.ToString(), "Export MLO Objects");

                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + outDir + "\""); } catch { }
            }
        }


        private static string CleanFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mlo";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }


        // ===================== MLO "void gallery" =====================
        // Lays every MLO archetype in the game out in a floating grid in an empty void
        // (the rest of the map is unloaded), so all interiors can be worked with at once.

        private ToolStripMenuItem mloGalleryMenuItem;
        private bool mloGalleryActive;
        private YmapFile mloGalleryYmap;
        private MetaHash mloGalleryYmapHash;
        private bool mloGallerySavedRenderworld;
        private bool mloGallerySavedRenderinteriors;
        private bool mloGallerySavedRendermloshells;

        private void MloToolsLoadAllMlos_Click(object sender, EventArgs e)
        {
            if (mloGalleryActive)
            {
                ExitMloGallery();
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                int count = BuildMloGallery();
                if (count <= 0)
                {
                    MessageBox.Show(this, "No MLO archetypes were found to load.", "Load All MLOs");
                    return;
                }

                mloGallerySavedRenderworld = renderworld;
                mloGallerySavedRenderinteriors = Renderer.renderinteriors;
                mloGallerySavedRendermloshells = Renderer.rendermloshells;
                Renderer.renderinteriors = true;   //interiors must render
                Renderer.rendermloshells = false;  //hide the MLO exterior/building shells (low-LOD towers etc.)
                renderworld = false;               //unload/stop streaming the real map
                mloGalleryActive = true;
                if (mloGalleryMenuItem != null) mloGalleryMenuItem.Text = "Exit MLO gallery (back to map)";

                // drop the camera into the gallery
                GoToPosition(mloGalleryCamPos);
                UpdateStatus("MLO gallery: " + count + " MLOs loaded into the void. Fly around to view them.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't build the MLO gallery:\n" + ex.Message, "Load All MLOs");
                mloGalleryActive = false;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ExitMloGallery()
        {
            mloGalleryActive = false;
            Renderer.renderinteriors = mloGallerySavedRenderinteriors;
            Renderer.rendermloshells = mloGallerySavedRendermloshells;
            renderworld = mloGallerySavedRenderworld;
            if (mloGalleryMenuItem != null) mloGalleryMenuItem.Text = "Load All MLOs (void gallery)";
            UpdateStatus("Exited MLO gallery.");
        }

        private Vector3 mloGalleryCamPos = Vector3.Zero;

        // Builds (once) a synthetic ymap holding one instance of every MLO archetype, laid
        // out in a single line sorted smallest -> largest. Returns the number of MLOs placed.
        private int BuildMloGallery()
        {
            if (mloGalleryYmap != null) return mloGalleryYmap.RootEntities?.Length ?? 0; //already built

            // gather every unique MLO archetype
            var mloArchs = new List<MloArchetype>();
            var seen = new HashSet<uint>();
            foreach (var ytyp in GameFileCache.YtypDict.Values)
            {
                if (ytyp?.AllArchetypes == null) continue;
                foreach (var arch in ytyp.AllArchetypes)
                {
                    if (arch is MloArchetype mloa)
                    {
                        if (seen.Add(mloa.Hash)) mloArchs.Add(mloa);
                    }
                }
            }
            if (mloArchs.Count == 0) return 0;

            // Order smallest -> largest by horizontal footprint, then lay them out in ONE line
            // along +X, advancing by each MLO's own width (+ a small gap) so small ones sit
            // close together, it grows to the biggest, and nothing overlaps.
            mloArchs.Sort((a, b) =>
            {
                var sa = a.BBMax - a.BBMin; var sb = b.BBMax - b.BBMin;
                return Math.Max(sa.X, sa.Y).CompareTo(Math.Max(sb.X, sb.Y));
            });

            const float gap = 10.0f;   //clearance between neighbours' bounding boxes
            const float baseY = 0.0f;
            const float baseZ = 50.0f; //float above the ground

            // build the synthetic ymap
            var ymap = new YmapFile();
            ymap.RpfFileEntry = new RpfResourceFileEntry();
            ymap.RpfFileEntry.Name = "mlo_gallery.ymap";
            ymap.Name = ymap.RpfFileEntry.Name;
            mloGalleryYmapHash = JenkHash.GenHash("mlo_gallery");
            ymap.RpfFileEntry.ShortNameHash = mloGalleryYmapHash;
            JenkIndex.Ensure("mlo_gallery");
            ymap.Loaded = true;
            ymap._CMapData.name = new MetaHash(mloGalleryYmapHash);
            ymap._CMapData.contentFlags = 65;

            int placed = 0;
            float cursorX = 0.0f;
            float prevHalf = 0.0f;
            for (int i = 0; i < mloArchs.Count; i++)
            {
                var mloa = mloArchs[i];
                var size = mloa.BBMax - mloa.BBMin;
                // Advance by each MLO's OWN half-width so every neighbour pair has exactly `gap`
                // clearance: no overlap, and small ones still sit close. Min keeps zero/degenerate
                // bounds from overlapping; the high max only catches a few broken/huge bounds so
                // one bad MLO can't blow a kilometre-wide hole in the line.
                float halfW = Math.Min(Math.Max(Math.Abs(size.X) * 0.5f, 3.0f), 250.0f);

                if (i == 0) cursorX = halfW;
                else cursorX += prevHalf + gap + halfW; //advance so edges (not centres) are spaced
                prevHalf = halfW;

                // place: centre along the line in X/Y, and rest each MLO's floor on baseZ so
                // they all sit at the same level -> reads as one clean line.
                var archCenter = (mloa.BBMin + mloa.BBMax) * 0.5f;
                var cellCenter = new Vector3(cursorX, baseY, baseZ);
                var instPos = new Vector3(cursorX - archCenter.X, baseY - archCenter.Y, baseZ - mloa.BBMin.Z);

                var cent = new CEntityDef();
                cent.archetypeName = mloa.Hash;
                cent.position = instPos;
                cent.rotation = new Vector4(0, 0, 0, 1);
                cent.scaleXY = 1.0f;
                cent.scaleZ = 1.0f;
                cent.flags = 0;
                cent.parentIndex = -1;
                cent.lodDist = 8000.0f; //render from a long way off
                cent.childLodDist = 8000.0f;
                cent.lodLevel = rage__eLodType.LODTYPES_DEPTH_ORPHANHD;
                cent.priorityLevel = rage__ePriorityLevel.PRI_REQUIRED;
                cent.ambientOcclusionMultiplier = 255;
                cent.artificialAmbientOcclusion = 255;

                var mlo = new CMloInstanceDef();
                mlo.CEntityDef = cent;

                var ent = new YmapEntityDef(ymap, placed, ref mlo);
                ent.SetArchetype(mloa); //creates MloInstance + interior entities at instPos
                if (ent.MloInstance != null)
                {
                    ent.MloInstance.InitYmapEntityArchetypes(GameFileCache); //resolve interior archetypes
                    // make every entity set visible so ALL props load, not just the defaults
                    if (ent.MloInstance.EntitySets != null)
                    {
                        foreach (var es in ent.MloInstance.EntitySets)
                        {
                            if (es != null) es.Visible = true;
                        }
                    }
                }
                ymap.AddEntity(ent);

                if (placed == 0)
                {
                    // camera start: a bit back and above the first (smallest) MLO
                    float depth = Math.Max(size.Y, 20.0f);
                    float high = Math.Max(size.Z, 10.0f);
                    mloGalleryCamPos = cellCenter + new Vector3(0, -(depth * 0.7f + 15.0f), high * 0.5f);
                }
                placed++;
            }

            lock (RenderSyncRoot)
            {
                mloGalleryYmap = ymap;
            }
            return placed;
        }

        // Called from the WorldForm render dispatch when the gallery is active.
        private void RenderMloGallery()
        {
            renderworldVisibleYmapDict.Clear();
            if (mloGalleryYmap != null)
            {
                renderworldVisibleYmapDict[mloGalleryYmapHash] = mloGalleryYmap;
            }

            Renderer.RenderWorld(renderworldVisibleYmapDict, null);

            foreach (var ymap in Renderer.VisibleYmaps)
            {
                UpdateMouseHits(ymap);
            }
        }

        private static string FmtVec(Vector3 v)
        {
            var c = CultureInfo.InvariantCulture;
            return v.X.ToString("0.######", c) + " " + v.Y.ToString("0.######", c) + " " + v.Z.ToString("0.######", c);
        }

        private static string FmtQuat(Quaternion q)
        {
            var c = CultureInfo.InvariantCulture;
            return q.X.ToString("0.######", c) + " " + q.Y.ToString("0.######", c) + " " +
                   q.Z.ToString("0.######", c) + " " + q.W.ToString("0.######", c);
        }
    }
}
