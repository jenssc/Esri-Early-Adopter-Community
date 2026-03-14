using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ICSharpCode.SharpZipLib.Zip;
using IO = System.IO;

namespace SimplifySample
{
    internal class Program
    {
        static void Main(string[] args) {
            ArcGIS.Core.Hosting.Host.Initialize(ArcGIS.Core.Hosting.Host.LicenseProductCode.ArcGISPro);

            Console.WriteLine("Hello, ArcGIS!");

            Console.WriteLine();
            Console.WriteLine($"ClippingSample.exe {string.Join(' ', args)}");
            Console.WriteLine();

            #region Arguments
            var arguments = new Dictionary<string, string>();

            for (int i = 0; i < args.Length; i++) {
                if (args[i].StartsWith("--")) {
                    var key = args[i].Substring(2);
                    var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        ? args[++i]
                        : "true";

                    arguments[key] = value;
                }
            }
            #endregion

            var target = @".\clippingsample.gdb";

            #region Initializing databases
            Console.WriteLine("Extracting sample database...");
            if (Directory.Exists(target))
                Directory.Delete(target, true);

            var filegeodatabase = "testing.gdb.zip";

            if (arguments.ContainsKey("input"))
                filegeodatabase = arguments["input"];

            FastZip fastZip = new();
            fastZip.ExtractZip(IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filegeodatabase), IO.Path.GetFullPath(target), null);

            var createGeodatabaseInstance = () => {
                var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(IO.Path.GetFullPath(target))));
                return geodatabase;
            };
            #endregion

            //  Create validation method used to check if all features has only one exterior ring
            var isValid = () => {
                using (var destination = createGeodatabaseInstance()) {
                    using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                        using (var cursor = surface.Search(null, true)) {
                            while (cursor.MoveNext()) {
                                var feature = (Feature)cursor.Current;
                                var objectid = feature.GetObjectID();
                                var shape = (Polygon)feature.GetShape();

                                if (shape.ExteriorRingCount > 1) {
                                    Console.WriteLine($"--- OID::{objectid} has multiple exterior rings #{shape.ExteriorRingCount}!");
                                    return false;
                                }
                            }
                        }
                    }
                }
                return true;
            };

            //  Do we have a valid database before we begin ?
            if (isValid())
                Console.WriteLine("Everything is valid, all set to go");
            else {
                Console.WriteLine("Houston, we have a problem!");
                return;
            }

            //  Geometry check
            using (var destination = createGeodatabaseInstance()) {
                using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                    using var cursor = surface.Search(null, true);
                    while (cursor.MoveNext()) {
                        var feature = (Feature)cursor.Current;

                        var shape = (Polygon)feature.GetShape();

                        if ((shape).ExteriorRingCount > 1) {
                            Console.WriteLine($"--- OID::{feature.GetObjectID()} has multiple exterior rings #{shape.ExteriorRingCount}!");
                            return;
                        }

                        var simple = GeometryEngine.Instance.SimplifyOgc(shape, true);
                        if (!simple.IsEqual(shape)) {
                            if (((Polygon)simple).ExteriorRingCount > 1) System.Diagnostics.Debugger.Break();
                            feature.SetShape(simple);
                            feature.Store();
                        }

                        //if (!isValid()) return;
                    }
                }
            }

            isValid();
        }
    }
}
