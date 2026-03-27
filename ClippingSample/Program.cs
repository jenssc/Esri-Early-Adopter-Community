using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ICSharpCode.SharpZipLib.Zip;
using System.Diagnostics;
using IO = System.IO;

namespace ClippingSample
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
            fastZip.ExtractZip(IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Source.gdb.zip"), IO.Path.GetFullPath(@".\source.gdb"), null);

            var createGeodatabaseInstance = () => {
                var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(IO.Path.GetFullPath(target))));
                return geodatabase;
            };
            #endregion

            Polygon[] clipping = [];

            #region Loading clipping polygons
            Console.WriteLine("Loading clipping polygons...");
            using (var source = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(IO.Path.GetFullPath(@".\source.gdb"))))) {
                using var productCoverage = source.OpenDataset<FeatureClass>("ProductCoverage");

                using var search = productCoverage.Search(new QueryFilter {
                    WhereClause = $"PLTS_COMP_SCALE < 22000",
                    PostfixClause = "ORDER BY PLTS_COMP_SCALE DESC",
                }, true);

                while (search.MoveNext()) {
                    var shape = (Polygon)((Feature)search.Current).GetShape();

                    var ring = shape.GetExteriorRing(0, true);
                    clipping = [.. clipping, ring];
                }
            }
            #endregion

            double area = double.MinValue;

            using (var destination = createGeodatabaseInstance()) {
                using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                    using var cursor = surface.CreateUpdateCursor(null, true);
                    while (cursor.MoveNext()) {
                        var feature = (Feature)cursor.Current;

                        var shape = (Polygon)feature.GetShape();
                        if (shape.Area > area)
                            area = shape.Area;
                    }
                }
            }

            //  Create validation method used to check if all features has only one exterior ring
            var isValid = () => {
                var success = true;
                using (var destination = createGeodatabaseInstance()) {
                    using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                        using (var cursor = surface.Search(null, true)) {
                            while (cursor.MoveNext()) {
                                var feature = (Feature)cursor.Current;
                                var objectid = feature.GetObjectID();
                                var shape = (Polygon)feature.GetShape();

                                if (shape.Area > area) {
                                    Console.WriteLine($"--- OID::{objectid} large area!");
                                    success = false;
                                }

                                if (shape.ExteriorRingCount > 1) {
                                    Console.WriteLine($"--- OID::{objectid} has multiple exterior rings #{shape.ExteriorRingCount}!");
                                    success = false;
                                }
                                if (!shape.IsKnownSimple) {
                                    Console.WriteLine($"--- OID::{objectid} is not know simple!");
                                    success = false;
                                }
                                if (!shape.IsKnownSimpleOgc) {
                                    //Console.WriteLine($"--- OID::{objectid} is not know simple OGC!");
                                }
                                if (shape.IsEmpty) {
                                    Console.WriteLine($"--- OID::{objectid} is empty!");
                                    success = false;
                                }
                            }
                        }
                    }
                }
                return success;
            };

            //  Do we have a valid database before we begin ?
            if (isValid())
                Console.WriteLine("Everything is valid, all set to go");
            else {
                Console.WriteLine("Houston, we have a problem!");
                return;
            }

            #region Clip polygons
            Console.WriteLine("Clipping polygon features...");
            using (var destination = createGeodatabaseInstance()) {
                int tripCounter = 0;
                foreach (var queryPolygon in clipping) {
                    Console.WriteLine($"  Clipping #{++tripCounter}");

                    using (var featureClass = destination.OpenDataset<FeatureClass>("point")) {
                        var targetSR = featureClass.GetDefinition().GetSpatialReference();
                        var queryPolygonProjected = (Polygon)GeometryEngine.Instance.Project(queryPolygon, targetSR);

                        var spatialFilter = new SpatialQueryFilter {
                            FilterGeometry = queryPolygonProjected,
                            SpatialRelationship = SpatialRelationship.Contains
                        };
                        featureClass.DeleteRows(spatialFilter);
                    }

                    using (var featureClass = destination.OpenDataset<FeatureClass>("pointset")) {
                        var targetSR = featureClass.GetDefinition().GetSpatialReference();
                        var queryPolygonProjected = (Polygon)GeometryEngine.Instance.Project(queryPolygon, targetSR);

                        var spatialFilter = new SpatialQueryFilter {
                            FilterGeometry = queryPolygonProjected,
                            SpatialRelationship = SpatialRelationship.Contains
                        };
                        featureClass.DeleteRows(spatialFilter);
                    }

                    {   //  curve
                        long[] hits = [];
                        using (var featureClass = destination.OpenDataset<FeatureClass>("curve")) {
                            var targetSR = featureClass.GetDefinition().GetSpatialReference();
                            var queryPolygonProjected = (Polygon)GeometryEngine.Instance.Project(queryPolygon, targetSR);

                            var spatialFilter = new SpatialQueryFilter {
                                FilterGeometry = queryPolygonProjected,
                                SpatialRelationship = SpatialRelationship.IndexIntersects
                            };

                            using (var cursor = featureClass.CreateUpdateCursor(spatialFilter, true)) {
                                while (cursor.MoveNext()) {
                                    hits = [.. hits, cursor.Current.GetObjectID()];
                                }
                            }
                        }

                        long[] updated = [];
                        long[] created = [];
                        long[] deleted = [];

                        using (var featureClass = destination.OpenDataset<FeatureClass>("curve")) {
                            var targetSR = featureClass.GetDefinition().GetSpatialReference();
                            var queryPolygonProjected = (Polygon)GeometryEngine.Instance.Project(queryPolygon, targetSR);

                            using var insert = featureClass.CreateInsertCursor();

                            foreach (var objectid in hits) {
                                using var cursor = featureClass.Search(new QueryFilter {
                                    WhereClause = $"OBJECTID = {objectid}",
                                }, false);

                                cursor.MoveNext();

                                using var feature = (Feature)cursor.Current;
                                var shape = (Polyline)feature.GetShape();

                                if (GeometryEngine.Instance.Disjoint(shape, queryPolygonProjected))
                                    continue;

                                if (GeometryEngine.Instance.Within(shape, queryPolygonProjected)) {
                                    deleted = [.. deleted, objectid];
                                }
                                else if (GeometryEngine.Instance.Intersects(queryPolygonProjected, shape)) {
                                    deleted = [.. deleted, objectid];
                                    var difference = GeometryEngine.Instance.Difference(shape, queryPolygonProjected);

                                    if (difference is Polyline polyline) {
                                        using var buffer = featureClass.CreateRowBuffer(feature);
                                        buffer["shape"] = polyline;
                                        var _ = insert.Insert(buffer);
                                        created = [.. created, _];
                                    }
                                }
                            }

                            insert.Flush();

                            featureClass.DeleteRows(new QueryFilter {
                                WhereClause = $"OBJECTID IN ({string.Join(',', deleted)})",
                            });

                            featureClass.DeleteRows(new SpatialQueryFilter {
                                FilterGeometry = queryPolygonProjected,
                                SpatialRelationship = SpatialRelationship.Contains
                            });
                        }
                    }


                    {   //  surface
                        long[] hits = [];
                        using (var featureClass = destination.OpenDataset<FeatureClass>("surface")) {
                            var targetSR = featureClass.GetDefinition().GetSpatialReference();
                            var queryPolygonProjected = (Polygon)GeometryEngine.Instance.Project(queryPolygon, targetSR);

                            var spatialFilter = new SpatialQueryFilter {
                                FilterGeometry = queryPolygonProjected,
                                SpatialRelationship = SpatialRelationship.IndexIntersects
                            };

                            using (var cursor = featureClass.CreateUpdateCursor(spatialFilter, true)) {
                                while (cursor.MoveNext()) {
                                    hits = [.. hits, cursor.Current.GetObjectID()];
                                }
                            }
                        }

                        long[] updated = [];
                        long[] created = [];
                        long[] deleted = [];

                        using (var featureClass = destination.OpenDataset<FeatureClass>("surface")) {
                            var targetSR = featureClass.GetDefinition().GetSpatialReference();
                            var queryPolygonProjected = (Polygon)GeometryEngine.Instance.Project(queryPolygon, targetSR);

                            using var insert = featureClass.CreateInsertCursor();

                            foreach (var objectid in hits) {
                                using var cursor = featureClass.Search(new QueryFilter {
                                    WhereClause = $"OBJECTID = {objectid}",
                                }, false);

                                cursor.MoveNext();

                                var feature = (Feature)cursor.Current;
                                var shape = (Polygon)feature.GetShape();

                                if (GeometryEngine.Instance.Within(shape, queryPolygonProjected)) {
                                    deleted = [.. deleted, objectid];
                                }
                                else if (GeometryEngine.Instance.Intersects(queryPolygonProjected, shape)) {
                                    deleted = [.. deleted, objectid];
                                    var difference = GeometryEngine.Instance.Difference(shape, queryPolygonProjected);

                                    if (difference is Polygon polygon) {
                                        if (polygon.IsEmpty) continue;

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

                                            using var buffer = featureClass.CreateRowBuffer(feature);
                                            for (int i = 0; i < polygons.Length; i++) {
                                                //buffer["shape"] = polygons[i];
                                                var p = GeometryEngine.Instance.SimplifyAsFeature(polygons[i]);
                                                Debug.Assert(p.IsKnownSimple);
                                                buffer["shape"] = p;
                                                var _ = insert.Insert(buffer);
                                                created = [.. created, _];

                                                if (objectid == 2160) {
                                                    using (var spare = destination.OpenDataset<FeatureClass>("surface_spare")) {
                                                        var b = spare.CreateRowBuffer();
                                                        b["shape"] = p;
                                                        spare.CreateRow(b);
                                                    }
                                                }
                                            }
                                            if (objectid == 2160)
                                                return;
                                        }
                                        else {
                                            using var buffer = featureClass.CreateRowBuffer(feature);
                                            //buffer["shape"] = polygon;
                                            var p = GeometryEngine.Instance.SimplifyAsFeature(polygon);
                                            Debug.Assert(p.IsKnownSimple);
                                            buffer["shape"] = p;
                                            var _ = insert.Insert(buffer);
                                            created = [.. created, _];
                                        }
                                    }
                                    else
                                        System.Diagnostics.Debugger.Break();
                                }

                                try {
                                    if (!isValid()) {
                                        Console.WriteLine($"... caused by OID {objectid}");
                                        return;
                                    }
                                }
                                catch (System.Exception ex) {
                                    Console.WriteLine($"Cautht exception: {ex}");
                                    Console.WriteLine($"... caused by OID {objectid}");
                                    return;
                                }
                            }

                            insert.Flush();

                            featureClass.DeleteRows(new QueryFilter {
                                WhereClause = $"OBJECTID IN ({string.Join(',', deleted)})",
                            });

                            featureClass.DeleteRows(new SpatialQueryFilter {
                                FilterGeometry = queryPolygonProjected,
                                SpatialRelationship = SpatialRelationship.Contains
                            });
                        }

                        Console.WriteLine($"\tcreated: OBJECTID IN ({string.Join(',', created)})");
                        Console.WriteLine($"\tdeleted: OBJECTID IN ({string.Join(',', deleted)})");

                    }

                    if (!isValid()) {
                        Console.WriteLine("Houston, we have a problem!");
                        return;
                    }
                }
            }
            #endregion

            Console.WriteLine("All great!");
        }
    }
}


namespace ArcGIS.Core.Geometry
{
    public static class Extension
    {
        public static ReadOnlySegmentCollection RemoveRepeatedVertices(this ReadOnlySegmentCollection segments) {
            LineSegment[] _ = [(LineSegment)segments[0]];

            for (int i = 1; i < segments.Count; i++) {
                if (segments[i - 1].StartPoint.IsEqual(segments[i].StartPoint) && segments[i - 1].EndPoint.IsEqual(segments[i].EndPoint)) continue;
                if (segments[i].StartPoint.IsEqual(segments[i].EndPoint)) continue;
                _ = [.. _, (LineSegment)segments[i]];
            }
            if (_.Length != segments.Count) System.Diagnostics.Debugger.Break();

            var polyline = PolylineBuilderEx.CreatePolyline(_, segments.SpatialReference);
            return polyline.Parts[0];
        }
    }
}