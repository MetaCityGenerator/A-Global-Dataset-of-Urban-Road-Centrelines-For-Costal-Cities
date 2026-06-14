using System.Globalization;
using MetaCity.DataProcessing;
using MetaCity.IO.GeoJSON;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Client
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(
                    "Usage: extractLine <input.geojson> <output.geojson> " +
                    "[--buffer B] [--seed S] [--grid G] [--epsilon E] [--segment T]");
                Environment.Exit(1);
            }

            try
            {
                string inputFile = args[0];
                string outputFile = args[1];

                // Optional manual parameter overrides (used by the sensitivity analysis).
                // If NONE are supplied, behaviour is unchanged: density-based AutoSelectParameters.
                double? mBuffer = null, mSeed = null, mGrid = null, mEpsilon = null, mSegment = null;
                double? mMinBuf = null, mMaxBuf = null;
                bool adaptive = false;
                for (int i = 2; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--adaptive": adaptive = true; break;
                        case "--buffer":  mBuffer = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--seed":    mSeed = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--grid":    mGrid = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--epsilon": mEpsilon = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--segment": mSegment = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--minbuf":  mMinBuf = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--maxbuf":  mMaxBuf = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    }
                }

                // Ensure output directory exists
                string outputDir = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(outputDir))
                    Directory.CreateDirectory(outputDir);

                FeatureCollection crvs = ReadWriteData.ReadGeoJSON(inputFile);

                double gridSize, bufferDist, interpolateDist, epsilon, segmentThreshold;
                bool manual = mBuffer.HasValue || mSeed.HasValue || mGrid.HasValue
                              || mEpsilon.HasValue || mSegment.HasValue || adaptive;
                if (manual)
                {
                    // Manual mode: use the supplied values, default the rest to reference constants.
                    gridSize         = mGrid    ?? 1000;
                    bufferDist       = mBuffer  ?? 15;
                    interpolateDist  = mSeed    ?? 15;
                    epsilon          = mEpsilon ?? 45;
                    segmentThreshold = mSegment ?? 10;
                    Console.WriteLine(
                        "ManualParameters: buffer_dist={0}, interpolate_dist={1}, epsilon={2}, segment_threshold={3}, grid_size={4}",
                        bufferDist, interpolateDist, epsilon, segmentThreshold, gridSize);
                }
                else
                {
                    // Auto-select parameters based on city road density
                    var (g, b, ip, e, s) = CenterLineExtraction.AutoSelectParameters(crvs);
                    gridSize = g; bufferDist = b; interpolateDist = ip; epsilon = e; segmentThreshold = s;
                }

                double minBuf = mMinBuf ?? 5, maxBuf = mMaxBuf ?? 50;
                if (adaptive)
                    Console.WriteLine("AdaptiveBuffer: ON  ref_buffer={0} (dense cells), min={1}, max={2} (sparse cells)",
                        bufferDist, minBuf, maxBuf);

                FeatureCollection result = CenterLineExtraction.Extract(crvs,
                    grid_size: gridSize, buffer_dist: bufferDist,
                    interpolate_dist: interpolateDist, epsilon: epsilon,
                    segment_threshold: segmentThreshold, PRECISION: 3,
                    adaptive_buffer: adaptive, min_buffer: minBuf, max_buffer: maxBuf);

                if (result.Count > 0)
                    ReadWriteData.WriteGeoJSON(result, outputFile);

                // Structured output for Python to parse
                Console.WriteLine($"RESULT:features={result.Count}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                Environment.Exit(2);
            }
        }

        public static FeatureCollection BuildFeatureCollection(NetTopologySuite.Geometries.Geometry[] geos)
        {
            var fc = new FeatureCollection();
            for (int i = 0; i < geos.Length; i++)
            {
                AttributesTable att = new AttributesTable { };
                Feature f = new Feature(geos[i], att);
                fc.Add(f);
            }
            return fc;
        }
    }
}
