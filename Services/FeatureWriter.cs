using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;

namespace GoBase.Services
{
	// Writes GeoJSON features straight into an existing feature class, without a geoprocessing tool,
	// so a page of features shows up as soon as it arrives. Everything here runs on the MCT.
	//
	// The feature class is made once per geometry type by JSONToFeatures, which picks its fields from
	// the first page. A property later pages bring that it has no field for is not kept.
	internal static class FeatureWriter
	{
		// Removes every feature. Outside an edit operation: nothing for Undo, no edits to save.
		public static void Clear(FeatureClass featureClass) =>
			GeodatabaseOf(featureClass).ApplyEdits(() => featureClass.DeleteRows(new QueryFilter()));

		// Returns how many were written. A feature whose geometry cannot be read is left out.
		public static int Insert(FeatureClass featureClass, IReadOnlyList<JsonElement> features)
		{
			var definition = featureClass.GetDefinition();
			var spatialReference = definition.GetSpatialReference();
			var shapeIndex = definition.FindField(definition.GetShapeField());
			var dimensions = new Dimensions(definition.HasZ(), definition.HasM());
			var fields = WritableFields(definition);
			var fieldFor = new Dictionary<string, WritableField>(StringComparer.Ordinal);

			var written = 0;
			var unreadable = 0;
			string firstProblem = null;
			GeodatabaseOf(featureClass).ApplyEdits(() =>
			{
				using var cursor = featureClass.CreateInsertCursor();
				using var buffer = featureClass.CreateRowBuffer();
				foreach (var feature in features)
				{
					var geometry = GeometryOf(feature, spatialReference, dimensions, out var problem);
					if (geometry == null)
					{
						unreadable++;
						firstProblem ??= problem;
						continue;
					}

					buffer[shapeIndex] = geometry;
					foreach (var field in fields)
						buffer[field.Index] = null;
					if (feature.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
					{
						foreach (var property in properties.EnumerateObject())
						{
							if (!fieldFor.TryGetValue(property.Name, out var field))
								fieldFor[property.Name] = field = FieldFor(fields, property.Name);
							if (field != null)
								buffer[field.Index] = ValueFor(field.Field, property.Value);
						}
					}

					cursor.Insert(buffer);
					written++;
				}
				cursor.Flush();
			});
			if (unreadable > 0)
				ErrorLog.Write($"GoBase: {unreadable:N0} of {features.Count:N0} features in {featureClass.GetName()} had a geometry Pro could not read, first: {firstProblem}");
			return written;
		}

		private record WritableField(Field Field, int Index, string[] Names);

		private static Geodatabase GeodatabaseOf(FeatureClass featureClass) =>
			featureClass.GetDatastore() as Geodatabase
			?? throw new InvalidOperationException("GoBase layers must be in a geodatabase.");

		// The attribute fields, with every name a property may go by: JSONToFeatures replaces characters
		// a field name cannot hold with underscores, and may keep the original as the alias.
		private static List<WritableField> WritableFields(FeatureClassDefinition definition) =>
			definition.GetFields()
				.Select((field, index) => (field, index))
				.Where(it => it.field.IsEditable && it.field.FieldType is not
					(FieldType.OID or FieldType.Geometry or FieldType.GlobalID or FieldType.Blob or FieldType.Raster or FieldType.XML))
				.Select(it => new WritableField(it.field, it.index,
					new[] { it.field.Name, it.field.AliasName }.Where(name => !string.IsNullOrEmpty(name)).ToArray()))
				.ToList();

		// The field a property goes in, or null when there is none: by its name, or as JSONToFeatures
		// renamed it, with an underscore for each character a field name cannot hold.
		private static WritableField FieldFor(List<WritableField> fields, string propertyName)
		{
			var safeName = Regex.Replace(propertyName, @"[^A-Za-z0-9_]", "_");
			return fields.FirstOrDefault(field => field.Names.Any(name =>
				string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(name, safeName, StringComparison.OrdinalIgnoreCase)));
		}

		// A property value in the field's type, or null when it does not fit.
		private static object ValueFor(Field field, JsonElement value)
		{
			if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
				return null;

			switch (field.FieldType)
			{
				case FieldType.String:
				{
					var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
					return field.Length > 0 && text.Length > field.Length ? text[..field.Length] : text;
				}
				case FieldType.SmallInteger:
					return Integer(value) is long small && small >= short.MinValue && small <= short.MaxValue ? (short)small : null;
				case FieldType.Integer:
					return Integer(value) is long integer && integer >= int.MinValue && integer <= int.MaxValue ? (int)integer : null;
				case FieldType.BigInteger:
					return Integer(value);
				case FieldType.Single:
					return Number(value) is double single ? (float)single : null;
				case FieldType.Double:
					return Number(value);
				case FieldType.Date:
					return value.ValueKind == JsonValueKind.String
						&& DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
							? date
							: null;
				case FieldType.TimestampOffset:
					return value.ValueKind == JsonValueKind.String
						&& DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
							? timestamp
							: null;
				case FieldType.GUID:
					return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guid)
						? guid.ToString("B").ToUpperInvariant()
						: null;
				default:
					return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
			}
		}

		private static long? Integer(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.Number when value.TryGetInt64(out var number) => number,
			JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
			JsonValueKind.True => 1,
			JsonValueKind.False => 0,
			_ => null
		};

		private static double? Number(JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.Number => value.GetDouble(),
			JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
			_ => null
		};

		// Builds the geometry straight from the GeoJSON coordinates, in WGS 84, then projects it if the
		// feature class is in another system. Null, with the reason, for one that cannot be read.
		internal static Geometry GeometryOf(
			JsonElement feature, SpatialReference spatialReference, Dimensions dimensions, out string problem)
		{
			problem = null;
			if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object
				|| !geometry.TryGetProperty("type", out var type)
				|| !geometry.TryGetProperty("coordinates", out var coordinates))
			{
				problem = "no geometry or coordinates";
				return null;
			}

			try
			{
				Geometry shape;
				switch (type.GetString())
				{
					case "Point":
					{
						var point = CoordinateOf(coordinates);
						shape = new MapPointBuilderEx(point.X, point.Y, dimensions.HasZ, point.Z, dimensions.HasM, double.NaN,
							false, 0, SpatialReferences.WGS84).ToGeometry();
						break;
					}
					case "MultiPoint":
						shape = new MultipointBuilderEx(CoordinatesOf(coordinates), SpatialReferences.WGS84)
							{ HasZ = dimensions.HasZ, HasM = dimensions.HasM }.ToGeometry();
						break;
					case "LineString":
						shape = Multipart(new PolylineBuilderEx(SpatialReferences.WGS84), dimensions, [CoordinatesOf(coordinates)]);
						break;
					case "MultiLineString":
						shape = Multipart(new PolylineBuilderEx(SpatialReferences.WGS84), dimensions,
							coordinates.EnumerateArray().Select(CoordinatesOf));
						break;
					case "Polygon":
						shape = Multipart(new PolygonBuilderEx(SpatialReferences.WGS84), dimensions, Rings(coordinates));
						break;
					case "MultiPolygon":
						shape = Multipart(new PolygonBuilderEx(SpatialReferences.WGS84), dimensions,
							coordinates.EnumerateArray().SelectMany(Rings));
						break;
					default:
						problem = $"geometry type {type.GetString()}";
						return null;
				}

				if (shape == null || shape.IsEmpty)
				{
					problem = "Pro read it as an empty geometry";
					return null;
				}
				if (spatialReference != null && !SpatialReference.AreEqual(shape.SpatialReference, spatialReference))
					shape = GeometryEngine.Instance.Project(shape, spatialReference);
				return shape;
			}
			// Any exception, ArcGIS's geometry errors included: one bad feature must not stop the rest.
			catch (Exception ex)
			{
				problem = $"{ex.GetType().Name}: {ex.Message}";
				return null;
			}
		}

		// Whether the feature class stores heights and measures. JSONToFeatures can make one with Z
		// values, and Pro refuses a geometry without them there.
		internal record Dimensions(bool HasZ, bool HasM);

		private static Geometry Multipart(
			MultipartBuilderEx builder, Dimensions dimensions, IEnumerable<List<Coordinate3D>> parts)
		{
			builder.HasZ = dimensions.HasZ;
			builder.HasM = dimensions.HasM;
			foreach (var part in parts)
				builder.AddPart(part);
			return builder.ToGeometry();
		}

		// A GeoJSON polygon's rings, turned the way ArcGIS wants them: the outer ring clockwise, holes
		// counter-clockwise. RFC 7946 asks for the opposite, and older GeoJSON for nothing at all, so
		// each ring is checked rather than all reversed.
		private static IEnumerable<List<Coordinate3D>> Rings(JsonElement polygon)
		{
			var outer = true;
			foreach (var ringElement in polygon.EnumerateArray())
			{
				var ring = CoordinatesOf(ringElement);
				if (IsClockwise(ring) != outer)
					ring.Reverse();
				outer = false;
				yield return ring;
			}
		}

		// The shoelace formula: a negative signed area is clockwise when y points up.
		private static bool IsClockwise(List<Coordinate3D> ring)
		{
			var twiceArea = 0.0;
			for (var i = 0; i < ring.Count; i++)
			{
				var a = ring[i];
				var b = ring[(i + 1) % ring.Count];
				twiceArea += (b.X - a.X) * (b.Y + a.Y);
			}
			return twiceArea > 0;
		}

		private static List<Coordinate3D> CoordinatesOf(JsonElement positions)
		{
			var coordinates = new List<Coordinate3D>(positions.GetArrayLength());
			foreach (var position in positions.EnumerateArray())
				coordinates.Add(CoordinateOf(position));
			return coordinates;
		}

		// [x, y] or [x, y, z]: a position without a height gets 0.
		private static Coordinate3D CoordinateOf(JsonElement position) => new(
			position[0].GetDouble(),
			position[1].GetDouble(),
			position.GetArrayLength() > 2 && position[2].ValueKind == JsonValueKind.Number ? position[2].GetDouble() : 0);
	}
}
