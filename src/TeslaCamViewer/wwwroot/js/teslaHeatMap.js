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

    function setHeat(points) {
        if (!map) {
            console.error("Map not initialized");
            return;
        }
        if (!L.heatLayer) {
            console.error("L.heatLayer missing - leaflet-heat.js not loaded");
            return;
        }

        if (heat) {
            console.log("Removing existing heatmap layer");
            heat.remove();
        }

        if (points.length === 0) {
            console.log("No points provided, skipping heatmap creation");
            return;
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
                radius: dynamicRadius,
                blur: dynamicBlur,
                maxZoom: 19,
                max: 1,
                minOpacity: 0.5,
                gradient: {
                    0.0: 'blue',
                    0.3: 'cyan',
                    0.5: 'lime',
                    0.7: 'yellow',
                    1.0: 'red'
                }
            };

            const latLngs = points
                .map(p => L.latLng(p[0], p[1]));

            heat = L.heatLayer(latLngs, heatOptions).addTo(map);

            const bounds = L.latLngBounds(latLngs);
            if (!bounds.isValid()) return;

            map.fitBounds(bounds.pad(0.2));

            // Force another invalidation after adding heatmap
            map.invalidateSize();
        }, 100);
    }

    return { init, setHeat };
})();