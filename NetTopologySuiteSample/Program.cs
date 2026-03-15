using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ICSharpCode.SharpZipLib.Zip;
using static System.Runtime.InteropServices.JavaScript.JSType;
using IO = System.IO;

namespace NetTopologySuiteSample
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
                    clipping = [.. clipping, shape];
                }
            }
            #endregion

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

                                var nettopology = shape.ToNetTopology();
                                var arcgisgeometry = nettopology.ToArcGIS();

                                if (shape.IsEqual(arcgisgeometry)) {
                                    Console.WriteLine($"--- OID::{objectid} is not NetTopologySuite!");
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

            using (var destination = createGeodatabaseInstance()) {
                using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                    using var cursor = surface.Search(null, true);
                    while (cursor.MoveNext()) {
                        var feature = (Feature)cursor.Current;

                        var shape = (Polygon)feature.GetShape();
                        var netTopology = shape.ToNetTopology();

                        feature.SetShape(netTopology.ToArcGIS());
                        feature.Store();
                    }
                }
            }

            #region Clip polygons
            Console.WriteLine("Clipping polygon features...");
            using (var destination = createGeodatabaseInstance()) {
                int tripCounter = 0;
                foreach (var queryPolygon in clipping) {
                    Console.WriteLine($"  Clipping #{++tripCounter}");

                    var spatialFilter = new SpatialQueryFilter {
                        FilterGeometry = queryPolygon,
                        SpatialRelationship = SpatialRelationship.IndexIntersects
                    };

                    long[] hits = [];
                    using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                        using (var cursor = surface.CreateUpdateCursor(spatialFilter, true)) {
                            while (cursor.MoveNext()) {
                                hits = [.. hits, cursor.Current.GetObjectID()];
                            }
                        }
                    }

                    var queryPolygonNetTopology = queryPolygon.ToNetTopology();

                    long[] deleted = [];
                    long[] updated = [];
                    using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                        using var insert = surface.CreateInsertCursor();

                        foreach (var objectid in hits) {
                            using var cursor = surface.Search(new QueryFilter {
                                WhereClause = $"OBJECTID = {objectid}",
                            }, false);

                            cursor.MoveNext();

                            //if (objectid == 1084) System.Diagnostics.Debugger.Break();

                            var feature = (Feature)cursor.Current;
                            var shape = ((Polygon)feature.GetShape()).ToNetTopology();

                            if (shape.Disjoint(queryPolygonNetTopology))
                                continue;

                            if (shape.Contains(queryPolygonNetTopology)) {
                                Console.WriteLine($"\tdelete::{objectid}");
                                deleted = [.. deleted, objectid];
                                continue;
                            }

                            var difference = shape.Difference(queryPolygonNetTopology);

                            if (difference is NetTopologySuite.Geometries.Polygon polygon) {
                                if (polygon.IsEmpty) {
                                    Console.WriteLine($"\tdelete::{objectid}");
                                    deleted = [.. deleted, objectid];
                                }
                                else {
                                    Console.WriteLine($"\tdelete::{objectid}");
                                    deleted = [.. deleted, objectid];
                                    using var buffer = surface.CreateRowBuffer(feature);
                                    buffer["shape"] = polygon.ToArcGIS();
                                    var uid = insert.Insert(buffer);
                                    Console.WriteLine($"\tinsert::{uid}");
                                    //using var _ = surface.CreateRow(buffer);
                                    //feature.SetShape(polygon.ToArcGIS());
                                    //feature.Store();
                                }
                            }
                            else if (difference is NetTopologySuite.Geometries.MultiPolygon multiPolygon) {
                                if (multiPolygon.Any(e => e.IsEmpty)) System.Diagnostics.Debugger.Break();

                                deleted = [.. deleted, objectid];
                                using var buffer = surface.CreateRowBuffer(feature);
                                buffer["shape"] = ((NetTopologySuite.Geometries.Polygon)multiPolygon[0]).ToArcGIS();
                                var uid = insert.Insert(buffer);
                                Console.WriteLine($"\tinsert::{uid}");

                                //feature.SetShape(((NetTopologySuite.Geometries.Polygon)multiPolygon[0]).ToArcGIS());
                                //feature.Store();

                                //using var buffer = surface.CreateRowBuffer(feature);
                                //buffer["ps"] = feature["ps"];
                                //buffer["code"] = feature["code"];
                                //buffer["flatten"] = feature["flatten"];
                                foreach (NetTopologySuite.Geometries.Polygon p in multiPolygon.Skip(1)) {
                                    buffer["shape"] = p.ToArcGIS();
                                    uid = insert.Insert(buffer);
                                    Console.WriteLine($"\tinsert::{uid}");
                                    //using var _ = surface.CreateRow(buffer);
                                }
                            }
                            else {
                                ;
                            }

                            //if (!isValid()) return;
                        }

                        insert.Flush();
                    }

                    if (deleted.Any()) {
                        using (var surface = destination.OpenDataset<FeatureClass>("surface")) {
                            surface.DeleteRows(new QueryFilter {
                                WhereClause = $"OBJECTID IN ({string.Join(',', deleted)})",
                            });
                        }
                    }

                    if (updated.Any())
                        Console.WriteLine($"\tUpdated: OBJECTID IN ({string.Join(',', updated)})");

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

namespace NetTopologySuite.Geometries
{
    public static class Extension
    {
        public static LineString RemoveRepeatedVertices(this LineString lineString) {
            var coordinates = lineString.Coordinates.RemoveRepeatedVertices();
            if (coordinates.Length != lineString.Count)
                return (LineString)lineString.Factory.CreateLineString(coordinates.ToArray());
            return lineString;
        }

        public static LinearRing RemoveRepeatedVertices(this LinearRing linearRing) {
            var coordinates = linearRing.Coordinates.RemoveRepeatedVertices();
            if (coordinates.Length != linearRing.Count)
                return (LinearRing)linearRing.Factory.CreateLinearRing(coordinates.ToArray());
            return linearRing;
        }

        public static Coordinate[] RemoveRepeatedVertices(this Coordinate[] coordinates) {
            var _ = new List<Coordinate> { coordinates[0] };

            for (int i = 1; i < coordinates.Length; i++) {
                if (coordinates[i - 1].Equals(coordinates[i])) continue;
                _.Add(coordinates[i]);
            }
            return _.ToArray();
        }
    }
}

namespace ArcGIS.Core.Geometry
{
    using NetTopologySuite.Geometries;

    public static class Extension
    {
        static readonly GeometryFactory factory = new GeometryFactory(new PrecisionModel(10000000), srid: 4326); // Or PrecisionModels.Floating

        public static NetTopologySuite.Geometries.Polygon ToNetTopology(this Polygon shape) {
            var exteriorRing = shape.GetExteriorRing(0);
            var coordinates = exteriorRing.Parts[0].Select(segment => new Coordinate(segment.StartPoint.X, segment.StartPoint.Y)).ToArray();

            var ex = factory.CreateLinearRing([.. coordinates, coordinates[0]]);
            ex = ex.RemoveRepeatedVertices();

            if (shape.PartCount > 1) {
                var interiorRings = new List<LinearRing>();

                foreach (var interiorRing in shape.Parts.Skip(1)) {
                    coordinates = interiorRing.Select(segment => new Coordinate(segment.StartPoint.X, segment.StartPoint.Y)).ToArray();

                    var linestring = factory.CreateLinearRing([.. coordinates, coordinates[0]]);
                    linestring = linestring.RemoveRepeatedVertices();
                    interiorRings.Add(linestring);
                }

                return factory.CreatePolygon(ex, [.. interiorRings]);
            }
            else {
                return factory.CreatePolygon(ex);
            }
        }

        public static ArcGIS.Core.Geometry.Polygon ToArcGIS(this NetTopologySuite.Geometries.Polygon polygon) {
            var sr = SpatialReferenceBuilder.CreateSpatialReference(polygon.SRID);

            // Outer ring
            var outerRing = polygon.ExteriorRing;
            var outerCoords = outerRing.RemoveRepeatedVertices().Coordinates.Select(c => new Coordinate2D(c.X, c.Y));

            var polygonBuilder = new PolygonBuilderEx(outerCoords);

            // Interior rings (holes)
            for (int i = 0; i < polygon.NumInteriorRings; i++) {
                var hole = polygon.GetInteriorRingN(i);
                var holeCoords = hole.RemoveRepeatedVertices().Coordinates.Select(c => new Coordinate2D(c.X, c.Y));

                polygonBuilder.AddPart(holeCoords);
            }

            return polygonBuilder.ToGeometry();
        }
    }
}