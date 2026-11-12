using APSIM.Shared.Utilities;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Rendering;
using Mapsui.Tiling.Layers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentationMap = APSIM.Shared.Documentation.Map;

namespace APSIM.Shared.Mapping
{
    /// <summary>
    /// This class can render world maps.
    /// </summary>
    public static class MapRenderer
    {
        /// <summary>
        /// Static constructor to perform 1-time initialisation.
        /// </summary>
        static MapRenderer()
        {
        }

        /// <remarks>
        /// The world of mapping and GIS is a rather specialised and complex sub-field. Our needs here
        /// are fairly simple: we just want to be able to plot locations on a base map. But how are locations
        /// specified? Where do we get the map? What projection do we use? These remarks are intended to 
        /// (slightly) clarify what is going on.
        /// 
        /// We are using Mapsui to do the map rendering, which in turn uses BruTile to fetch a suitable base map. The basemap 
        /// tiles use a "projected" coordinate system, specifically EPSG 3857 (also known as Web Mercator); 
        /// the units in this system are (perhaps surpisingly) metres. However, the point data we wish to plot
        /// is expressed as latitude and longitude (using a "geographic" coordinate system, specifically EPSG 4326),
        /// with units of decimal degrees. Note that both are based on WGS84, so they have the same underlying
        /// model of the shape of the earth, but use vastly different units. 
        /// </remarks>
        /// <summary>
        /// Indicates the ratio between steps when zooming.
        /// </summary>
        private const double zoomStepFactor = 1.5;

        /// <summary>
        /// Render the map as a <see cref="SkiaSharp.SKImage"/> of the specified size.
        /// </summary>
        /// <param name="map">Map tag to be rendered.</param>
        /// <param name="width">Width of the map in px.</param>
        public static SkiaSharp.SKImage ToImage(this DocumentationMap map, int width)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Map width must be greater than zero.");

            Map exported = map.ToMapsuiMap();
            var center = SphericalMercator.FromLonLat(map.Center.Longitude, map.Center.Latitude);
            exported.Navigator.CenterOn(new MPoint(center.x, center.y));
            exported.Navigator.SetSize(width, width);

            // Preserve the historical APSIM zoom convention while using the map-owned
            // navigator required by the current Mapsui rendering API.
            double zoom = map.Zoom - 1.0;
            if (zoom >= 60.0 || zoom < 0.0)
                zoom = 0.0;
            double resolution = (78271.51696401953125 * 512 / width) /
                                Math.Pow(GetZoomStepFactor(), zoom);
            exported.Navigator.ZoomTo(resolution);

            using MemoryStream bitmap = new Mapsui.Rendering.Skia.MapRenderer()
                .RenderToBitmapStream(exported, 1f, RenderFormat.Png);
            bitmap.Position = 0;
            SkiaSharp.SKImage result = SkiaSharp.SKImage.FromEncodedData(bitmap);
            return result ?? throw new InvalidDataException("Mapsui returned an invalid map image.");
        }

        /// <summary>
        /// Create a <see cref="Map"/> representing this map object.
        /// </summary>
        /// <param name="map">A map to be exported/rendered.</param>
        public static Map ToMapsuiMap(this DocumentationMap map)
        {
            Map result = InitMap();

            GenericCollectionLayer<List<IFeature>> markerLayer = new GenericCollectionLayer<List<IFeature>>
            {
                Name = "Markers"
            };
            markerLayer.Style = new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = 0.65,
                Fill = new Brush(Color.Red),
                Outline = new Pen(Color.White, 2),
                Offset = new Offset(0, 0)
            };

            foreach (var loc in map.Markers.Select(c => SphericalMercator.FromLonLat(c.Longitude, c.Latitude)))
                markerLayer.Features.Add(new GeometryFeature(new NetTopologySuite.Geometries.Point(loc.x, loc.y)));
            result.Layers.Add(markerLayer);

            return result;
        }


        /// <summary>
        /// Get the zoom step factor. This is the ratio of each zoom increment to
        /// the previous zoom increment.
        /// </summary>
        /// <returns></returns>
        public static double GetZoomStepFactor() => zoomStepFactor;

        /// <summary>
        /// Initialise the map component.
        /// </summary>
        private static Map InitMap()
        {
            Map result = new Map
            {
                CRS = "EPSG:3857",
                BackColor = Mapsui.Styles.Color.FromString("LightBlue")
            };

            Mapsui.Tiling.Layers.TileLayer osmLayer = OpenStreetMap.CreateTileLayer("APSIM Next Generation");
            if (osmLayer.TileSource is BruTile.Web.HttpTileSource)
            {
                BruTile.Cache.FileCache fileCache = new BruTile.Cache.FileCache(Path.Combine(Path.GetTempPath(), "OSM Map Tiles"), "png", new TimeSpan(100, 0, 0, 0));
                (osmLayer.TileSource as BruTile.Web.HttpTileSource).PersistentCache = fileCache;
            }
            result.Layers.Add(osmLayer);

            // This layer is used only as a sort of backup in case the BruTile download times out
            // or is otherwise unavailable.
            // It should normally be invisible, as it will be covered by the BruTile tile layer.
            string apsimx = PathUtilities.GetAbsolutePath("%root%", null);
            string shapeFileName = Path.Combine(apsimx, "ApsimNG", "Resources", "world", "countries.shp");
            if (File.Exists(shapeFileName))
            {
                Layer layWorld = new Layer("Countries");
                Mapsui.Nts.Providers.Shapefile.ShapeFile shapeFile = new Mapsui.Nts.Providers.Shapefile.ShapeFile(shapeFileName, true)
                {
                    CRS = "EPSG:4326"
                };
                ProjectingProvider provider = new ProjectingProvider(shapeFile)
                { 
                    CRS = "EPSG:3857" 
                };
                layWorld.DataSource = provider;
                layWorld.Style = new Mapsui.Styles.VectorStyle
                {
                    Outline = new Mapsui.Styles.Pen { Color = Mapsui.Styles.Color.Black },
                    Fill = new Mapsui.Styles.Brush { Color = Mapsui.Styles.Color.White }
                };
                layWorld.Style.Enabled = true;
                layWorld.Enabled = false;
                result.Layers.Insert(0, layWorld);
            }
            return result;
        }
    }
}
