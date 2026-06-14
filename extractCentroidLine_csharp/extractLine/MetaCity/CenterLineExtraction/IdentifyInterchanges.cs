using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using MetaCity.IO.GeoJSON;


namespace MetaCity.DataProcessing
{
    public class IdentifyInterchanges
    {
        public FeatureCollection BoxFeatures { get; private set; }

        public IList<Envelope> Boxes { get; private set; }

        public IdentifyInterchanges(FeatureCollection roads)
        {
            Stopwatch sw = new();
            sw.Start();

            //string dicName = "../../../../MetaCity/DataProcessing/CenterLineExtraction/Python";
            string dicName = "./Python";
            string outputDir = $"{dicName}/HighWay_output";
            
            string pythonFile = Path.Combine(dicName, "HighwayDetection_deploy", $"Program.py " +
                //$"--input_file {""} " +
                $"--output_dir {outputDir} " +
                $"--weights { Path.Combine(dicName, "HighwayDetection_deploy", "weights", "yolo_highway.pt")}");
            string pythonExecLine = $"python {pythonFile}";

            GeoJsonWriter geoJsonWriter = new();
            GeoJsonReader geoJsonReader = new();
            string extractResultsString = geoJsonWriter.Write(roads);

            ProcessStartInfo start = new()
            {
                FileName = @"C:\Windows\System32\cmd.exe",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            string boxResultString;

            using (Process myProcess = Process.Start(start))
            {
                StreamWriter writer = myProcess.StandardInput;
                StreamReader reader = myProcess.StandardOutput;
                string result;

                do result = reader.ReadLine();
                while (result != "");

                writer.WriteLine("activate yoloEnv");
                result = reader.ReadLine();
                result = reader.ReadLine();
                writer.WriteLine(pythonExecLine);
                result = reader.ReadLine();

                writer.WriteLine(extractResultsString);

                do boxResultString = reader.ReadLine();
                while (!boxResultString.StartsWith("{"));

                writer.Close();
                reader.Close();

                myProcess.WaitForExit();
            };

            BoxFeatures = geoJsonReader.Read<FeatureCollection>(boxResultString);
            Boxes = FeatureCollectionToEnvelopes(BoxFeatures);

            sw.Stop();
            Console.WriteLine("Identifying all interchanges: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));
        }

        private static IList<Envelope> FeatureCollectionToEnvelopes(FeatureCollection fc)
        {
            List<Envelope> res = new();
            foreach (Feature f in fc)
                if (f.Geometry.GeometryType == Geometry.TypeNameMultiPolygon)
                    foreach (Geometry geom in ((MultiPolygon)f.Geometry).Geometries)
                        res.Add(((Polygon)geom).EnvelopeInternal);
                else
                    res.Add(((Polygon)f.Geometry).EnvelopeInternal);
            return res;
        }

        public FeatureCollection GetBoxFeaturesOverConfidence(double threshold)
        {
            FeatureCollection res = new();
            foreach (Feature f in BoxFeatures)
            {
                double confidence = (double)f.Attributes["confidence"];
                if (confidence >= threshold)
                    res.Add(f);
            }

            return res;
        }

        public IList<Envelope> GetBoxesOverConfidence(double threshold)
        {
            IList<Envelope> res = new List<Envelope>();
            for (int i = 0; i < BoxFeatures.Count; i++)
            {
                double confidence = (double)BoxFeatures[i].Attributes["confidence"];
                if (confidence >= threshold)
                    res.Add(Boxes[i]);
            }

            return res;
        }
    }
}
