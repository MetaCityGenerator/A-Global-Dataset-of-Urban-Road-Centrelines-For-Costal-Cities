using System;
using System.IO;
using System.Text;
using System.Text.Json;

using MetaCity.IO.GeoJSON.Converters;

namespace MetaCity.IO.GeoJSON
{
    public class GeoJsonReader
    {
        public GeoJsonConverterFactory ConverterFactory { get; }

        public JsonSerializerOptions SerializerOptions { get; }


        /// <summary>
        /// Creates an instance of this class using the defaults.
        /// </summary>
        public GeoJsonReader()
        {
            // Using default settings.
            ConverterFactory = new GeoJsonConverterFactory();

            SerializerOptions = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
            SerializerOptions.Converters.Add(ConverterFactory);
        }


        /// <summary>
        /// Creates an instance of this class using the provided <see cref="GeoJsonConverterFactory"/> and
        /// <see cref="JsonSerializerOptions"/>.
        /// </summary>
        /// <param name="factory">The factory to use when creating geometries.</param>
        public GeoJsonReader(GeoJsonConverterFactory factory)
        {
            ConverterFactory = factory;

            SerializerOptions = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
            SerializerOptions.Converters.Add(ConverterFactory);
        }


        /// <summary>
        /// Reading the specified json.
        /// </summary>
        /// <typeparam name="T">The type of the object.</typeparam>
        /// <param name="geoJson">The geoJson.</param>
        /// <returns>The deserialized object.</returns>
        public T Read<T>(string geoJson)
            where T : class
        {
            if (geoJson == null)
                throw new ArgumentNullException(nameof(geoJson));

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(geoJson));
            return Deserialize<T>(ms, SerializerOptions);
        }


        /// <summary>
        /// Helper method for reading specified memory stream.
        /// </summary>
        /// <typeparam name="T">The type of the object.</typeparam>
        /// <param name="stream">The memory stream.</param>
        /// <param name="options">Serializer option.</param>
        /// <returns>The deserialized object.</returns>
        private T Deserialize<T>(MemoryStream stream, JsonSerializerOptions options)
            where T : class
        {
            var b = new ReadOnlySpan<byte>(stream.ToArray());
            var r = new Utf8JsonReader(b);

            // we are at None
            r.Read();
            var res = JsonSerializer.Deserialize<T>(ref r, options);

            return res;
        }
    }
}
