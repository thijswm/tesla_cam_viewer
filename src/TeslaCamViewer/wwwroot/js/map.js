// Leaflet map management for Tesla Cam Viewer
let eventMap = null;
let eventMarker = null;

window.initializeMap = function(mapElementId, lat, lon) {
    // Clean up existing map if it exists
    if (eventMap) {
        eventMap.remove();
        eventMap = null;
        eventMarker = null;
    }

    // Validate coordinates
    if (!lat || !lon || isNaN(lat) || isNaN(lon)) {
        console.warn('Invalid coordinates:', lat, lon);
        return false;
    }

    try {
        // Initialize map centered on the event location
        eventMap = L.map(mapElementId, {
            zoomControl: true,
            attributionControl: true
        }).setView([lat, lon], 15); // Zoom level 15 for street-level view

        // Add OpenStreetMap tile layer
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '© OpenStreetMap contributors',
            maxZoom: 19
        }).addTo(eventMap);

        // Add red circle marker at event location
        eventMarker = L.circleMarker([lat, lon], {
            color: 'red',
            fillColor: '#f03',
            fillOpacity: 0.8,
            radius: 8,
            weight: 2
        }).addTo(eventMap);

        // Add popup with coordinates
        eventMarker.bindPopup(`Event Location<br>Lat: ${lat.toFixed(6)}<br>Lon: ${lon.toFixed(6)}`);

        console.log('Map initialized successfully at', lat, lon);
        return true;
    } catch (error) {
        console.error('Failed to initialize map:', error);
        return false;
    }
};

window.updateMapLocation = function(lat, lon) {
    if (!eventMap) {
        console.warn('Map not initialized');
        return false;
    }

    if (!lat || !lon || isNaN(lat) || isNaN(lon)) {
        console.warn('Invalid coordinates:', lat, lon);
        return false;
    }

    try {
        // Update map center
        eventMap.setView([lat, lon], 15);

        // Remove old marker
        if (eventMarker) {
            eventMarker.remove();
        }

        // Add new marker
        eventMarker = L.circleMarker([lat, lon], {
            color: 'red',
            fillColor: '#f03',
            fillOpacity: 0.8,
            radius: 8,
            weight: 2
        }).addTo(eventMap);

        eventMarker.bindPopup(`Event Location<br>Lat: ${lat.toFixed(6)}<br>Lon: ${lon.toFixed(6)}`);

        console.log('Map updated to', lat, lon);
        return true;
    } catch (error) {
        console.error('Failed to update map:', error);
        return false;
    }
};

window.destroyMap = function() {
    if (eventMap) {
        eventMap.remove();
        eventMap = null;
        eventMarker = null;
        console.log('Map destroyed');
    }
};
