using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ICSharpCode.SharpZipLib.Zip;
using System.Diagnostics;
using IO = System.IO;

namespace ClippingSAmple
{
    internal class Program
    {
        static void Main(string[] args) {
            ArcGIS.Core.Hosting.Host.Initialize(ArcGIS.Core.Hosting.Host.LicenseProductCode.ArcGISPro);

            Console.WriteLine("Hello, ArcGIS!");

            Console.WriteLine();
            Console.WriteLine($"ClippingSample.exe {string.Join(' ', args)}");
            Console.WriteLine();

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

            var target = @".\clippingsample.gdb";

            Console.WriteLine("Extracting sample database...");
            if (Directory.Exists(target))
                Directory.Delete(target, true);

            var filegeodatabase = "working.gdb.zip";

            if(arguments.ContainsKey("input"))
                filegeodatabase = arguments["input"];

            FastZip fastZip = new();
            fastZip.ExtractZip(IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filegeodatabase), IO.Path.GetFullPath(target), null);
            fastZip.ExtractZip(IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Source.gdb.zip"), IO.Path.GetFullPath(@".\source.gdb"), null);

            var createGeodatabaseInstance = () => {
                var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(IO.Path.GetFullPath(target))));
                return geodatabase;
            };

            Polygon[] clipping = [];

            Console.WriteLine("Loading clipping polygons...");
            using (var source = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(IO.Path.GetFullPath(@".\source.gdb"))))) {
                using var productCoverage = source.OpenDataset<FeatureClass>("ProductCoverage");

                using var search = productCoverage.Search(new QueryFilter {
                    WhereClause = $"PLTS_COMP_SCALE < 22000",
                    PostfixClause = "ORDER BY PLTS_COMP_SCALE DESC",
                }, true);

                while (search.MoveNext()) {
                    var shape = (Polygon)((Feature)search.Current).GetShape();

                    clipping = [.. clipping, shape.GetExteriorRing(0)];
                }
            }

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

            if (isValid())
                Console.WriteLine("Everything is valid, all set to go");
            else {
                Console.WriteLine("Houston, we have a problem!");
                return;
            }

            Console.WriteLine("Clipping polygon features...");
            using (var destination = createGeodatabaseInstance()) {
                int tripCounter = 0;
                foreach (var queryPolygon in clipping) {
                    Console.WriteLine($"  Clipping #{++tripCounter}");

                    var spatialFilter = new SpatialQueryFilter {
                        FilterGeometry = queryPolygon,
                    };

                    spatialFilter.SpatialRelationship = SpatialRelationship.IndexIntersects;

                    long[] hits = [];
                    using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                        using (var cursor = surface.CreateUpdateCursor(spatialFilter, true)) {
                            while (cursor.MoveNext()) {
                                hits = [.. hits, cursor.Current.GetObjectID()];
                            }
                        }
                    }

                    using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                        foreach (var objectid in hits) {
                            using var cursor = surface.Search(new QueryFilter {
                                WhereClause = $"OBJECTID = {objectid}",
                            }, false);

                            cursor.MoveNext();

                            var feature = (Feature)cursor.Current;
                            var shape = (Polygon)feature.GetShape();

                            if (GeometryEngine.Instance.Within(shape, queryPolygon)) {
                                feature.Delete();
                            }
                            else if (GeometryEngine.Instance.Intersects(queryPolygon, shape)) {
                                //Console.WriteLine($"    update::{objectid}");
                                var difference = GeometryEngine.Instance.Difference(shape, queryPolygon);

                                if (difference is Polygon polygon) {
                                    if (polygon.ExteriorRingCount > 1) {
                                        Polygon[] polygons = [];
                                        ReadOnlySegmentCollection[] segments = [polygon.Parts[0]];
                                        for (int i = 1; i < polygon.PartCount; i++) {
                                            var p = PolygonBuilderEx.CreatePolygon(polygon.Parts[i]);
                                            if (p.Area < 0)
                                                segments = [.. segments, polygon.Parts[i]];
                                            else {
                                                var _ = PolygonBuilderEx.CreatePolygon(segments);
                                                polygons = [.. polygons, _];
                                                segments = [polygon.Parts[i]];
                                            }
                                        }
                                        if (segments.Any()) {
                                            var _ = PolygonBuilderEx.CreatePolygon(segments);
                                            polygons = [.. polygons, _];
                                        }

                                        feature.SetShape(GeometryEngine.Instance.SimplifyAsFeature(polygons[0], true));
                                        feature.Store();

                                        using var buffer = surface.CreateRowBuffer();
                                        buffer["ps"] = feature["ps"];
                                        buffer["code"] = feature["code"];
                                        buffer["flatten"] = feature["flatten"];

                                        for (int i = 1; i < polygons.Length; i++) {
                                            var p = GeometryEngine.Instance.SimplifyAsFeature(polygons[i], true);

                                            buffer["shape"] = p;
                                            using var _ = surface.CreateRow(buffer);
                                        }
                                        buffer.Dispose();
                                    }
                                    else {
                                        feature.SetShape(GeometryEngine.Instance.SimplifyAsFeature(polygon, true));
                                        feature.Store();
                                    }
                                }
                            }
                        }
                    }

                    if (!isValid()) {
                        Console.WriteLine("Houston, we have a problem!");
                        return;
                    }
                }
            }

            Console.WriteLine("All great!");
        }
    }
}
