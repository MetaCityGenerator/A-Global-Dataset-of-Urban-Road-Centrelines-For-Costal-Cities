using System;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using MetaCity.IO.GeoJSON.Converters;


namespace MetaCity.IO.GeoJSON
{
    public class ReadWriteData
    {
        public static FeatureCollection ReadCSVPoint(string path)
        {
            StreamReader sr = new(path);
            int i = 0;
            List<string> headers = new();
            int colNum = 0;
            FeatureCollection fc = new();
            while (!sr.EndOfStream)
            {
                string s = sr.ReadLine();
                List<string> items = SplitRowByComma(s);
                if (i == 0)
                {
                    headers = items;
                    colNum = headers.Count;
                } else
                {
                    double lng = 0;
                    double lat = 0;
                    AttributesTable attrs = new();
                    for (int j = 0; j<colNum; j++)
                    {
                        string this_name = headers[j];
                        string this_value = items[j];
                        if (this_name == "lng") { lng = double.Parse(this_value); }
                          else if (this_name == "lat") { lat = double.Parse(this_value); }
                          else { attrs.Add(this_name, this_value); }
                    }
                    Point p = new(lng, lat);
                    Feature f = new(p, attrs);
                    fc.Add(f);
                }
                i++;
            }
            return fc;
        }

        private static List<string> SplitRowByComma(string s)
        {
            List<string> res = new();
            StringBuilder sb = new();
            bool withinQuotion = false;
            for (int k = 0; k<s.Length; k++)
            {
                char c = s[k];
                if (c == '"') withinQuotion = !withinQuotion;
                if (c == ',' && !withinQuotion)
                {
                    AddNewStringToList(res, ref sb);
                } else
                {
                    sb.Append(c);
                }
            }
            AddNewStringToList(res, ref sb);
            return res;
        }

        private static void AddNewStringToList(List<string> list, ref StringBuilder sb)
        {
            string this_string = sb.ToString();
            int i = 0;
            while (this_string[i] == '"') i++;
            int j = this_string.Length - 1;
            while (this_string[j] == '"') j--;
            this_string = this_string.Substring(i, j - i + 1);
            list.Add(this_string);
            sb.Clear();
        }

        public static FeatureCollection ReadGeoJSON(string path)
        {
            PrecisionModel pm = new PrecisionModel(1E+6);

            // Get stream from file (allow shared read so QGIS can keep the file open).
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Get JsonDOM from stream.
            using JsonDocument document = JsonDocument.Parse(stream);

            // Get srid (optional — GeoPandas GeoJSON exports don't include "crs").
            JsonElement root = document.RootElement;
            int srid = 0;
            if (root.TryGetProperty("crs", out JsonElement crsElement) &&
                crsElement.TryGetProperty("properties", out JsonElement propsElement) &&
                propsElement.TryGetProperty("name", out JsonElement nameElement))
            {
                var crs = nameElement.GetString();
                if (crs != null && crs.Contains("EPSG::"))
                    srid = Convert.ToInt32(crs.Split("EPSG::")[1]);
            }

            // Setting JsonSerializerOptions.
            GeoJsonConverterFactory gcf = new GeoJsonConverterFactory(new GeometryFactory(pm, srid), true);

            JsonSerializerOptions option = new JsonSerializerOptions()
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
            };

            option.Converters.Add(gcf);


            // Create Utf8JsonReader from span of bytes.
            byte[] fileBytes;
            using (var fs2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileBytes = new byte[fs2.Length];
                fs2.ReadExactly(fileBytes);
            }
            ReadOnlySpan<byte> bs = fileBytes;

            Utf8JsonReader r = new Utf8JsonReader(bs);
            r.Read();

            return JsonSerializer.Deserialize<FeatureCollection>(ref r, option);
        }

        public static void WriteGeoJSON(FeatureCollection featureCollection, string jsonFilePath)
        {
            if (featureCollection == null || featureCollection.Count == 0)
            {
                Console.Error.WriteLine("Warning: empty FeatureCollection, writing empty GeoJSON.");
                File.WriteAllText(jsonFilePath, "{\"type\":\"FeatureCollection\",\"features\":[]}");
                return;
            }

            JsonSerializerOptions options = new();
            options.Converters.Add(new GeoJsonConverterFactory(new(new PrecisionModel(1E+6))));
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

            FeatureCollectionConverter converter = new();
            if (File.Exists(jsonFilePath))
                File.Delete(jsonFilePath);
            using Utf8JsonWriter writer = new(File.OpenWrite(jsonFilePath));
            converter.Write(writer, featureCollection, options);
        }

        private static Dictionary<string, string> GetAttrsAbbreviated(IEnumerable<string> tooLongAttrs)
        {
            const int ATTR_MAX_LENGTH = 10;
            Dictionary<string, int> freq = new();
            foreach (string s in tooLongAttrs)
            {
                string subs = s.Substring(0, ATTR_MAX_LENGTH);
                if (!freq.ContainsKey(subs)) { freq.Add(subs, 1); }
                else freq[subs]++;
            }
            Dictionary<string, int> freq_counter = new();
            foreach (string s in freq.Keys) freq_counter.Add(s, 0);

            Dictionary<string, string> res = new();
            foreach (string s in tooLongAttrs)
            {
                string subs = s.Substring(0, ATTR_MAX_LENGTH);
                int f = freq[subs];
                if (f == 1)
                {
                    res.Add(s, subs);
                } else
                {
                    int d = f.ToString().Length;
                    string subs_prefix = subs.Substring(0, subs.Length - d);
                    string subs_suffix = freq_counter[subs].ToString();
                    int zero_nums = d - subs_suffix.Length;

                    string final_subs = "";
                    for (int i = 0; i < zero_nums; i++) final_subs += "0";
                    final_subs = subs_prefix + final_subs + subs_suffix;

                    res.Add(s, final_subs);
                    freq_counter[subs]++;
                }
            }
            return res;
        }
    }
}
