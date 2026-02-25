window.teslaHeatmap = (function () {
    let map, heat;

    function init(elementId, centerLat, centerLon, zoom) {
        const el = document.getElementById(elementId);
        if (!el) return;

        if (map) {
            map.setView([centerLat, centerLon], zoom);
            map.invalidateSize(true);
            return;
        }

        if (el._leaflet_id) el._leaflet_id = null;

        map = L.map(elementId).setView([centerLat, centerLon], zoom);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; OpenStreetMap'
        }).addTo(map);

        // Update heatmap radius when zoom changes
        map.on('zoomend', () => {
            if (heat) {
                updateHeatmapRadius();
            }
        });
    }

    function _cleanPoints(points) {
        if (!Array.isArray(points)) return [];
        const clean = [];
        for (const p of points) {
            // p must be an array [lat, lon, weight?]
            if (!Array.isArray(p) || p.length < 2) continue;
            const lat = Number(p[0]);
            const lon = Number(p[1]);
            if (!Number.isFinite(lat) || !Number.isFinite(lon)) continue;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180) continue;
            clean.push([lat, lon, p.length >= 3 ? Number(p[2]) || 1 : 1]);
        }
        return clean;
    }

    function updateHeatmapRadius() {
        if (!map || !heat) return;
        
        const zoom = map.getZoom();
        // Scale radius based on zoom: larger radius when zoomed out
        // At zoom 5 (far out): radius ~60, at zoom 15 (close): radius ~25
        const radius = Math.max(25, Math.min(80, 100 - (zoom * 4)));
        const blur = Math.max(15, radius * 0.6);
        
        heat.setOptions({
            radius: radius,
            blur: blur
        });
    }

    function setHeat(points, radius, blur, maxZoom) {
        if (!map) {
            console.error("Map not initialized");
            return;
        }
        if (!L.heatLayer) { 
            console.error("L.heatLayer missing - leaflet-heat.js not loaded"); 
            return; 
        }

        const clean = _cleanPoints(points);
        console.log("setHeat - input points:", points?.length, "cleaned points:", clean.length);
        
        if (clean.length > 0) {
            console.log("Sample cleaned points:", clean.slice(0, 3));
        }

        if (clean.length === 0) {
            console.warn("No valid points to display");
            if (heat) { heat.remove(); heat = null; }
            return;
        }

        if (heat) {
            console.log("Removing existing heatmap layer");
            heat.remove();
        }
        
        // CRITICAL: Ensure map is properly sized before creating heatmap
        // This fixes the "source width is 0" canvas error
        map.invalidateSize();
        
        // Small delay to let the map finish resizing
        setTimeout(() => {
            // Calculate initial radius based on current zoom
            const zoom = map.getZoom();
            const dynamicRadius = Math.max(25, Math.min(80, 100 - (zoom * 4)));
            const dynamicBlur = Math.max(15, dynamicRadius * 0.6);
            
            const heatOptions = {
                radius: radius ?? dynamicRadius,
                blur: blur ?? dynamicBlur,
                maxZoom: maxZoom ?? 19,
                max: 1.0,
                minOpacity: 0.5,
                gradient: {
                    0.0: 'blue',
                    0.3: 'cyan',
                    0.5: 'lime',
                    0.7: 'yellow',
                    1.0: 'red'
                }
            };
            
            console.log("Creating heatmap with options:", heatOptions);
            heat = L.heatLayer(clean, heatOptions).addTo(map);
            console.log("Heatmap layer added to map");
            
            // Force another invalidation after adding heatmap
            map.invalidateSize();
        }, 100);
    }

    function fitToPoints(points) {
        if (!map) return;

        // If called with a single point like [lat, lon, w], wrap it
        if (Array.isArray(points) && points.length >= 2 && typeof points[0] === "number") {
            points = [points];
        }

        if (!Array.isArray(points) || points.length === 0) return;

        const latLngs = points
            .filter(p => Array.isArray(p) && p.length >= 2 && Number.isFinite(Number(p[0])) && Number.isFinite(Number(p[1])))
            .map(p => L.latLng(Number(p[0]), Number(p[1])));

        if (latLngs.length === 0) return;

        const bounds = L.latLngBounds(latLngs);
        if (!bounds.isValid()) return;

        map.fitBounds(bounds.pad(0.2));
    }

    return { init, setHeat, fitToPoints };
})();