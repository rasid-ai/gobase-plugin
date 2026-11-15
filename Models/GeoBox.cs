using System;
using System.Globalization;

namespace GoBase.Models
{
	// A longitude/latitude rectangle (EPSG:4326), the shape of GoBase-Web's bbox parameter.
	internal sealed record GeoBox(double MinLon, double MinLat, double MaxLon, double MaxLat)
	{
		// Clamps to valid ranges; null when nothing is left (a map zoomed out past the world edge projects outside them).
		public static GeoBox Clamp(double minLon, double minLat, double maxLon, double maxLat)
		{
			minLon = Math.Clamp(minLon, -180, 180);
			maxLon = Math.Clamp(maxLon, -180, 180);
			minLat = Math.Clamp(minLat, -90, 90);
			maxLat = Math.Clamp(maxLat, -90, 90);

			if (double.IsNaN(minLon) || double.IsNaN(minLat) || minLon >= maxLon || minLat >= maxLat)
				return null;
			return new GeoBox(minLon, minLat, maxLon, maxLat);
		}

		// True when other lies inside this box. A tolerance of about a metre absorbs the jitter of
		// projecting the same map view twice.
		public bool Contains(GeoBox other)
		{
			const double tolerance = 1e-5;
			return other.MinLon >= MinLon - tolerance && other.MinLat >= MinLat - tolerance
				&& other.MaxLon <= MaxLon + tolerance && other.MaxLat <= MaxLat + tolerance;
		}

		// True when both boxes are the same view, give or take the same tolerance.
		public bool SameAs(GeoBox other) => Contains(other) && other.Contains(this);

		public static GeoBox FromArray(double[] bbox) =>
			bbox is { Length: 4 } ? new GeoBox(bbox[0], bbox[1], bbox[2], bbox[3]) : null;

		// "minLon,minLat,maxLon,maxLat". Invariant culture: on a French or Arabic Windows the
		// default would write "35,5" for 35.5 and break the comma-separated list.
		public string ToQueryValue() => string.Join(",",
			F(MinLon), F(MinLat), F(MaxLon), F(MaxLat));

		private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
	}
}
