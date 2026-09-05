using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.UI.Services;

namespace GeoSemanticSat.UI.Controls;

public enum MapMarkerState
{
    Candidate,
    Selected,
    Confirmed,
    Rejected,
    NeedsReview,
    SearchCenter,
    FacilityCenter
}

public class MapPin
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required double Latitude { get; set; }
    public required double Longitude { get; set; }
    public MapMarkerState State { get; set; } = MapMarkerState.Candidate;
    public string ChangeType { get; set; } = "Construction";
    public double Confidence { get; set; } = 0.95;
    public double AreaSqM { get; set; } = 25600;
    public BoundingBox? Bounds { get; set; }
    public object? Tag { get; set; }
}

public class MapFacilityCluster
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required double CenterLat { get; set; }
    public required double CenterLon { get; set; }
    public required double RadiusKm { get; set; }
    public List<MapPin> Pins { get; set; } = new();
    public double TotalAreaSqM { get; set; }
}

/// <summary>
/// High-Performance Interactive 2D Geographic Map Canvas for Satellite Analysts.
/// Features hybrid offline-first tile rendering with dynamic background fetching when online.
/// Supports smooth panning, mouse-wheel zooming, search-radius rings, candidate pins with status indicators,
/// facility clustering bounds, hover coordinate readout, and pin-click selection synchronization.
/// Designed with zero decorative emojis for clean military and intelligence presentation.
/// </summary>
public class InteractiveMapCanvas : Control
{
    private double _centerLat = 28.6050;
    private double _centerLon = 77.2080;
    private double _zoomLevel = 13.5;

    private bool _isDragging = false;
    private Point _lastDragPos;

    private readonly List<MapPin> _pins = new();
    private readonly List<MapFacilityCluster> _facilities = new();
    private MapPin? _selectedPin = null;

    private double? _searchCenterLat = 28.6050;
    private double? _searchCenterLon = 77.2080;
    private double _searchRadiusKm = 5.0;

    private readonly HybridTileService _tileService;
    public HybridTileService TileService => _tileService;

    public event EventHandler<MapPin>? PinSelected;
    public event EventHandler<GeoCoordinate>? CoordinateHovered;
    public event EventHandler<GeoCoordinate>? MapClicked;

    private static readonly Typeface TypefaceSans = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface TypefaceRegular = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    public InteractiveMapCanvas()
    {
        ClipToBounds = true;
        _tileService = new HybridTileService();
        _tileService.TileAvailable += () => Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public void SetCenterAndRadius(double lat, double lon, double radiusKm)
    {
        _searchCenterLat = lat;
        _searchCenterLon = lon;
        _searchRadiusKm = radiusKm;
        _centerLat = lat;
        _centerLon = lon;
        InvalidateVisual();
    }

    public void SetPins(IEnumerable<MapPin> pins)
    {
        _pins.Clear();
        _pins.AddRange(pins);
        InvalidateVisual();
    }

    public void SetFacilities(IEnumerable<MapFacilityCluster> facilities)
    {
        _facilities.Clear();
        _facilities.AddRange(facilities);
        InvalidateVisual();
    }

    public void SelectPin(string? pinId)
    {
        _selectedPin = _pins.FirstOrDefault(p => p.Id == pinId);
        if (_selectedPin != null)
        {
            _centerLat = _selectedPin.Latitude;
            _centerLon = _selectedPin.Longitude;
        }
        InvalidateVisual();
    }

    public void FitToAll()
    {
        if (_pins.Count == 0 && !_searchCenterLat.HasValue) return;

        double minLat = _pins.Count > 0 ? _pins.Min(p => p.Latitude) : _searchCenterLat!.Value;
        double maxLat = _pins.Count > 0 ? _pins.Max(p => p.Latitude) : _searchCenterLat!.Value;
        double minLon = _pins.Count > 0 ? _pins.Min(p => p.Longitude) : _searchCenterLon!.Value;
        double maxLon = _pins.Count > 0 ? _pins.Max(p => p.Longitude) : _searchCenterLon!.Value;

        if (_searchCenterLat.HasValue && _searchCenterLon.HasValue)
        {
            minLat = Math.Min(minLat, _searchCenterLat.Value - 0.03);
            maxLat = Math.Max(maxLat, _searchCenterLat.Value + 0.03);
            minLon = Math.Min(minLon, _searchCenterLon.Value - 0.03);
            maxLon = Math.Max(maxLon, _searchCenterLon.Value + 0.03);
        }

        _centerLat = (minLat + maxLat) * 0.5;
        _centerLon = (minLon + maxLon) * 0.5;

        double latSpan = Math.Max(0.01, maxLat - minLat);
        double lonSpan = Math.Max(0.01, maxLon - minLon);

        double span = Math.Max(latSpan, lonSpan);
        _zoomLevel = Math.Clamp(1.2 / span, 5.0, 22.0);

        InvalidateVisual();
    }

    public void ZoomIn()
    {
        _zoomLevel = Math.Min(_zoomLevel * 1.3, 35.0);
        InvalidateVisual();
    }

    public void ZoomOut()
    {
        _zoomLevel = Math.Max(_zoomLevel / 1.3, 2.0);
        InvalidateVisual();
    }

    public void SetBasemapProvider(BasemapProvider provider)
    {
        _tileService.ActiveProvider = provider;
        InvalidateVisual();
    }

    public void SetTileMode(MapTileMode mode)
    {
        _tileService.Mode = mode;
        InvalidateVisual();
    }

    private string _networkStatusText = "CONNECTED (AUTO)";
    public void UpdateConnectivityStatus(bool isOnline, double latencyMs)
    {
        _networkStatusText = isOnline ? $"ONLINE ({latencyMs:F0}ms)" : "OFFLINE / AIR-GAPPED";
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 500 : Math.Max(250, availableSize.Width);
        double h = double.IsInfinity(availableSize.Height) ? 400 : Math.Max(250, availableSize.Height);
        return new Size(w, h);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y > 0) ZoomIn();
        else if (e.Delta.Y < 0) ZoomOut();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _lastDragPos = pt.Position;

            var clickedPin = HitTestPin(pt.Position);
            if (clickedPin != null)
            {
                _selectedPin = clickedPin;
                PinSelected?.Invoke(this, clickedPin);
                InvalidateVisual();
            }
            else
            {
                var geo = ScreenToGeo(pt.Position);
                MapClicked?.Invoke(this, geo);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);

        if (_isDragging)
        {
            var delta = pt.Position - _lastDragPos;
            _lastDragPos = pt.Position;

            double scale = GetPixelsPerDegree();
            _centerLon -= delta.X / scale;
            _centerLat += delta.Y / scale;

            InvalidateVisual();
        }
        else
        {
            var geo = ScreenToGeo(pt.Position);
            CoordinateHovered?.Invoke(this, geo);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
    }

    private double GetPixelsPerDegree()
    {
        return Math.Pow(2.0, _zoomLevel) * 0.5;
    }

    private Point GeoToScreen(double lat, double lon)
    {
        double scale = GetPixelsPerDegree();
        double cx = Bounds.Width * 0.5;
        double cy = Bounds.Height * 0.5;

        double x = cx + (lon - _centerLon) * scale;
        double y = cy - (lat - _centerLat) * scale;

        return new Point(x, y);
    }

    private GeoCoordinate ScreenToGeo(Point pt)
    {
        double scale = GetPixelsPerDegree();
        double cx = Bounds.Width * 0.5;
        double cy = Bounds.Height * 0.5;

        double lon = _centerLon + (pt.X - cx) / scale;
        double lat = _centerLat - (pt.Y - cy) / scale;

        return new GeoCoordinate(lat, lon);
    }

    private MapPin? HitTestPin(Point screenPos)
    {
        foreach (var pin in _pins)
        {
            var pinPos = GeoToScreen(pin.Latitude, pin.Longitude);
            var delta = pinPos - screenPos;
            double dist = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
            if (dist <= 16) return pin;
        }
        return null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 1. Draw Map Canvas Dark Background
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#08090C")), null, new Rect(0, 0, w, h));

        // 2. Draw Hybrid Basemap Tiles (Offline Disk Cache + Background Online Fetch)
        DrawBasemapTiles(context, w, h);

        // 3. Draw Coordinate Grid Lines
        DrawCoordinateGrid(context, w, h);

        // 4. Draw Facility Clusters & Bounds
        DrawFacilityClusters(context);

        // 5. Draw Search Center and Radius Ring
        DrawSearchRadius(context);

        // 6. Draw Change Candidate Polygons & Pins
        DrawCandidatePins(context);

        // 7. Draw Coordinate Readout, Map Mode HUD & Symbology Legend
        DrawMapTelemetry(context, w, h);
    }

    private void DrawBasemapTiles(DrawingContext context, double w, double h)
    {
        int z = Math.Clamp((int)Math.Floor(_zoomLevel), 1, _tileService.ActiveProvider.MaxZoom);

        var topLeftGeo = ScreenToGeo(new Point(0, 0));
        var bottomRightGeo = ScreenToGeo(new Point(w, h));

        var (tMinX, tMinY) = HybridTileService.LonLatToTile(topLeftGeo.Longitude, topLeftGeo.Latitude, z);
        var (tMaxX, tMaxY) = HybridTileService.LonLatToTile(bottomRightGeo.Longitude, bottomRightGeo.Latitude, z);

        int minX = Math.Min(tMinX, tMaxX) - 1;
        int maxX = Math.Max(tMinX, tMaxX) + 1;
        int minY = Math.Min(tMinY, tMaxY) - 1;
        int maxY = Math.Max(tMinY, tMaxY) + 1;

        int n = 1 << z;
        minX = Math.Clamp(minX, 0, n - 1);
        maxX = Math.Clamp(maxX, 0, n - 1);
        minY = Math.Clamp(minY, 0, n - 1);
        maxY = Math.Clamp(maxY, 0, n - 1);

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                var (nwLon, nwLat) = HybridTileService.TileToLonLat(x, y, z);
                var (seLon, seLat) = HybridTileService.TileToLonLat(x + 1, y + 1, z);

                var pTopLeft = GeoToScreen(nwLat, nwLon);
                var pBottomRight = GeoToScreen(seLat, seLon);
                var destRect = new Rect(pTopLeft, pBottomRight);

                var bmp = _tileService.GetTile(z, x, y);
                if (bmp != null)
                {
                    context.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), destRect);
                }
                else
                {
                    var tileBrush = new SolidColorBrush(Color.FromArgb(255, 13, 15, 20));
                    var tilePen = new Pen(new SolidColorBrush(Color.FromArgb(25, 56, 189, 248)), 0.5);
                    context.DrawRectangle(tileBrush, tilePen, destRect);
                }
            }
        }
    }

    private void DrawCoordinateGrid(DrawingContext context, double w, double h)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1.0, DashStyle.Dash);

        double stepDeg = Math.Max(0.005, 0.5 / Math.Pow(2.0, _zoomLevel - 10.0));

        double minLon = ScreenToGeo(new Point(0, h)).Longitude;
        double maxLon = ScreenToGeo(new Point(w, 0)).Longitude;
        double minLat = ScreenToGeo(new Point(0, h)).Latitude;
        double maxLat = ScreenToGeo(new Point(w, 0)).Latitude;

        double startLon = Math.Floor(minLon / stepDeg) * stepDeg;
        double endLon = Math.Ceiling(maxLon / stepDeg) * stepDeg;
        double startLat = Math.Floor(minLat / stepDeg) * stepDeg;
        double endLat = Math.Ceiling(maxLat / stepDeg) * stepDeg;

        for (double lon = startLon; lon <= endLon; lon += stepDeg)
        {
            var pTop = GeoToScreen(maxLat, lon);
            var pBottom = GeoToScreen(minLat, lon);
            context.DrawLine(gridPen, pTop, pBottom);

            var text = new FormattedText($"{lon:F3} E", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceRegular, 9.0, new SolidColorBrush(Color.Parse("#52525B")));
            context.DrawText(text, new Point(pBottom.X + 3, h - 14));
        }

        for (double lat = startLat; lat <= endLat; lat += stepDeg)
        {
            var pLeft = GeoToScreen(lat, minLon);
            var pRight = GeoToScreen(lat, maxLon);
            context.DrawLine(gridPen, pLeft, pRight);

            var text = new FormattedText($"{lat:F3} N", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceRegular, 9.0, new SolidColorBrush(Color.Parse("#52525B")));
            context.DrawText(text, new Point(4, pLeft.Y - 12));
        }
    }

    private void DrawSearchRadius(DrawingContext context)
    {
        if (!_searchCenterLat.HasValue || !_searchCenterLon.HasValue) return;

        var centerPt = GeoToScreen(_searchCenterLat.Value, _searchCenterLon.Value);

        double pixelsPerKm = GetPixelsPerDegree() / 111.32;
        double radiusPx = _searchRadiusKm * pixelsPerKm;

        var circleFill = new SolidColorBrush(Color.FromArgb(18, 59, 130, 246));
        var circleStroke = new Pen(new SolidColorBrush(Color.FromArgb(160, 59, 130, 246)), 1.5, DashStyle.Dash);
        context.DrawEllipse(circleFill, circleStroke, centerPt, radiusPx, radiusPx);

        var centerStroke = new Pen(new SolidColorBrush(Color.Parse("#38BDF8")), 2.0);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#38BDF8")), null, centerPt, 5, 5);
        context.DrawLine(centerStroke, new Point(centerPt.X - 10, centerPt.Y), new Point(centerPt.X + 10, centerPt.Y));
        context.DrawLine(centerStroke, new Point(centerPt.X, centerPt.Y - 10), new Point(centerPt.X, centerPt.Y + 10));

        var label = new FormattedText($"Search Center ({_searchRadiusKm:F1} km)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceSans, 10.0, new SolidColorBrush(Color.Parse("#38BDF8")));
        context.DrawText(label, new Point(centerPt.X + 12, centerPt.Y - 6));
    }

    private void DrawFacilityClusters(DrawingContext context)
    {
        foreach (var fac in _facilities)
        {
            var centerPt = GeoToScreen(fac.CenterLat, fac.CenterLon);
            double pixelsPerKm = GetPixelsPerDegree() / 111.32;
            double radiusPx = Math.Max(25.0, fac.RadiusKm * pixelsPerKm);

            var fill = new SolidColorBrush(Color.FromArgb(24, 139, 92, 246));
            var stroke = new Pen(new SolidColorBrush(Color.FromArgb(140, 167, 139, 250)), 1.5, DashStyle.Dot);
            context.DrawEllipse(fill, stroke, centerPt, radiusPx, radiusPx);

            var text = new FormattedText($"Facility: {fac.Name} ({fac.Pins.Count} sites)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceSans, 10.5, new SolidColorBrush(Color.Parse("#C4B5FD")));
            context.DrawText(text, new Point(centerPt.X - radiusPx * 0.7, centerPt.Y - radiusPx - 14));
        }
    }

    private void DrawCandidatePins(DrawingContext context)
    {
        for (int i = 0; i < _pins.Count; i++)
        {
            var pin = _pins[i];
            var pt = GeoToScreen(pin.Latitude, pin.Longitude);
            bool isSelected = (_selectedPin?.Id == pin.Id);

            if (pin.Bounds != null)
            {
                var bounds = pin.Bounds.Value;
                var pTopLeft = GeoToScreen(bounds.MaxLat, bounds.MinLon);
                var pBottomRight = GeoToScreen(bounds.MinLat, bounds.MaxLon);
                var rect = new Rect(pTopLeft, pBottomRight);

                var boxFill = isSelected ? new SolidColorBrush(Color.FromArgb(60, 239, 68, 68)) : new SolidColorBrush(Color.FromArgb(30, 239, 68, 68));
                var boxStroke = new Pen(GetColorBrushForState(pin.State, isSelected), isSelected ? 2.5 : 1.5);
                context.DrawRectangle(boxFill, boxStroke, rect);
            }

            var pinBrush = GetColorBrushForState(pin.State, isSelected);
            double pinRadius = isSelected ? 9.0 : 7.0;

            if (isSelected)
            {
                var highlightStroke = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 2.0);
                context.DrawEllipse(null, highlightStroke, pt, 14, 14);
            }

            context.DrawEllipse(pinBrush, new Pen(new SolidColorBrush(Color.Parse("#000000")), 1.5), pt, pinRadius, pinRadius);

            string pinLabel = $"#{i + 1} {pin.ChangeType}";
            var labelText = new FormattedText(pinLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceSans, 10.0, isSelected ? new SolidColorBrush(Color.Parse("#FFFFFF")) : new SolidColorBrush(Color.Parse("#E4E4E7")));
            
            var badgeRect = new Rect(pt.X + pinRadius + 4, pt.Y - 8, labelText.Width + 8, labelText.Height + 4);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 18, 18, 22)), new Pen(pinBrush, 1.0), badgeRect);
            context.DrawText(labelText, new Point(badgeRect.X + 4, badgeRect.Y + 2));
        }
    }

    private static IBrush GetColorBrushForState(MapMarkerState state, bool isSelected)
    {
        if (isSelected) return new SolidColorBrush(Color.Parse("#38BDF8"));

        return state switch
        {
            MapMarkerState.Confirmed => new SolidColorBrush(Color.Parse("#10B981")),
            MapMarkerState.Rejected => new SolidColorBrush(Color.Parse("#EF4444")),
            MapMarkerState.NeedsReview => new SolidColorBrush(Color.Parse("#F59E0B")),
            MapMarkerState.SearchCenter => new SolidColorBrush(Color.Parse("#3B82F6")),
            MapMarkerState.FacilityCenter => new SolidColorBrush(Color.Parse("#8B5CF6")),
            _ => new SolidColorBrush(Color.Parse("#F43F5E")),
        };
    }

    private void DrawMapTelemetry(DrawingContext context, double w, double h)
    {
        var hudRect = new Rect(10, h - 34, 520, 24);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 15, 15, 18)), new Pen(new SolidColorBrush(Color.Parse("#27272A")), 1), hudRect);

        string modeStr = _tileService.Mode switch
        {
            MapTileMode.OfflineStrict => "AIR-GAPPED",
            MapTileMode.OnlinePreferred => "ONLINE PREFERRED",
            _ => "HYBRID AUTO"
        };

        var coordsText = new FormattedText($"Center: {_centerLat:F4} N, {_centerLon:F4} E | Z: {_zoomLevel:F1} | {_tileService.ActiveProvider.Name} | [{modeStr}] | [{_networkStatusText}]", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceRegular, 9.5, new SolidColorBrush(Color.Parse("#A1A1AA")));
        context.DrawText(coordsText, new Point(18, h - 28));

        var legendRect = new Rect(w - 230, 10, 220, 80);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 15, 18)), new Pen(new SolidColorBrush(Color.Parse("#27272A")), 1), legendRect);

        var legTitle = new FormattedText("MAP SYMBOLOGY", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceSans, 9.5, new SolidColorBrush(Color.Parse("#71717A")));
        context.DrawText(legTitle, new Point(w - 220, 16));

        context.DrawEllipse(new SolidColorBrush(Color.Parse("#38BDF8")), null, new Point(w - 215, 36), 4, 4);
        context.DrawText(new FormattedText("Search Center / Selected", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceRegular, 9.5, new SolidColorBrush(Color.Parse("#D4D4D8"))), new Point(w - 205, 30));

        context.DrawEllipse(new SolidColorBrush(Color.Parse("#F43F5E")), null, new Point(w - 215, 52), 4, 4);
        context.DrawText(new FormattedText("AI Detected Change", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceRegular, 9.5, new SolidColorBrush(Color.Parse("#D4D4D8"))), new Point(w - 205, 46));

        context.DrawEllipse(new SolidColorBrush(Color.Parse("#10B981")), null, new Point(w - 215, 68), 4, 4);
        context.DrawText(new FormattedText("Verified Confirmed Site", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TypefaceRegular, 9.5, new SolidColorBrush(Color.Parse("#D4D4D8"))), new Point(w - 205, 62));
    }
}
