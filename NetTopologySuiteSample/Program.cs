using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ICSharpCode.SharpZipLib.Zip;
using NetTopologySuite.Operation.Valid;
using System.Diagnostics;
using System.Security.Cryptography;
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

                                var validator = new IsValidOp(nettopology);
                                // If TRUE (Default): The Figure-8 is considered VALID.
                                // If FALSE: The Figure-8 is considered INVALID (Self-intersection).
                                validator.SelfTouchingRingFormingHoleValid = false;

                                if (!validator.IsValid) {
                                    var result = validator.ValidationError;

                                    var index = Array.IndexOf(nettopology.Coordinates, result.Coordinate);

                                    Console.WriteLine($"--- OID::{objectid} invalid polygon ({result.ErrorType})!");
                                    Console.WriteLine($"         {index} @{result.Coordinate}");
                                    success = false;
                                }
                               

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

                        var queryPolygonNetTopology = queryPolygon.ToNetTopology();

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
                                var shape = ((Polygon)feature.GetShape()).ToNetTopology();

                                if (shape.Disjoint(queryPolygonNetTopology))
                                    continue;

                                if (queryPolygonNetTopology.Contains(shape)) {
                                    deleted = [.. deleted, objectid];
                                }                                
                                else if (queryPolygonNetTopology.Intersects(shape)) {
                                    deleted = [.. deleted, objectid];
                                    var difference = shape.Difference(queryPolygonNetTopology);

                                    if (difference is NetTopologySuite.Geometries.Polygon polygon) {
                                        if (polygon.IsEmpty) continue;

                                        using var buffer = featureClass.CreateRowBuffer(feature);
                                        buffer["shape"] = polygon.ToArcGIS();
                                        var _ = insert.Insert(buffer);
                                        created = [.. created, _];
                                    }
                                    else if (difference is NetTopologySuite.Geometries.MultiPolygon multiPolygon) {
                                        if (multiPolygon.Any(e => e.IsEmpty)) continue;

                                        using var buffer = featureClass.CreateRowBuffer(feature);
                                        foreach (NetTopologySuite.Geometries.Polygon p in multiPolygon) {
                                            buffer["shape"] = p.ToArcGIS();
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

        public static LineString? MakePrecise(this LineString? lineString) {
            if (lineString == null) return lineString;

            for (int i = 0; i < lineString.NumPoints; i++) {
                lineString.Factory.PrecisionModel.MakePrecise(lineString[i]);
            }
            return lineString;
        }

        public static LinearRing MakePrecise(this LinearRing linearRing) {
            for (int i = 0; i < linearRing.NumPoints; i++) {
                linearRing.Factory.PrecisionModel.MakePrecise(linearRing[i]);
            }
            return linearRing;
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
            ex = ex.RemoveRepeatedVertices().MakePrecise();

            if (shape.PartCount > 1) {
                var interiorRings = new List<LinearRing>();

                foreach (var interiorRing in shape.Parts.Skip(1)) {
                    coordinates = interiorRing.Select(segment => new Coordinate(segment.StartPoint.X, segment.StartPoint.Y)).ToArray();

                    var linestring = factory.CreateLinearRing([.. coordinates, coordinates[0]]);
                    linestring = linestring.RemoveRepeatedVertices().MakePrecise();
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