// Video playlist manager for Tesla Cam Viewer
// Handles sequential playback of multiple video segments per camera

const videoPlaylists = {};
const currentIndices = {};

window.initializeVideoPlaylists = function(playlistData) {
    console.log('Initializing video playlists:', playlistData);
    
    // Store playlist data
    Object.assign(videoPlaylists, playlistData);
    
    // Initialize indices
    Object.keys(playlistData).forEach(camera => {
        currentIndices[camera] = 0;
    });
    
    // Set up event listeners for each video element
    Object.keys(playlistData).forEach(camera => {
        const videoElement = document.getElementById(`video-${camera}`);
        if (videoElement) {
            // Remove old listeners
            videoElement.removeEventListener('ended', handleVideoEnded);
            
            // Add new listener
            videoElement.addEventListener('ended', handleVideoEnded);
            
            // Set initial source
            loadClipForCamera(camera, 0);
        }
    });
};

function handleVideoEnded(event) {
    const videoElement = event.target;
    const camera = videoElement.dataset.camera;
    
    if (!camera || !videoPlaylists[camera]) {
        console.log('No playlist found for camera:', camera);
        return;
    }
    
    const playlist = videoPlaylists[camera];
    const currentIndex = currentIndices[camera];
    const nextIndex = currentIndex + 1;
    
    if (nextIndex < playlist.length) {
        console.log(`Loading next clip for ${camera}: index ${nextIndex}/${playlist.length}`);
        currentIndices[camera] = nextIndex;
        loadClipForCamera(camera, nextIndex);
        
        // Auto-play next clip
        videoElement.play().catch(err => console.log('Auto-play failed:', err));
    } else {
        console.log(`Playlist complete for ${camera}`);
    }
}

function loadClipForCamera(camera, index) {
    const playlist = videoPlaylists[camera];
    if (!playlist || index >= playlist.length) {
        return;
    }
    
    const clipId = playlist[index];
    const videoElement = document.getElementById(`video-${camera}`);
    
    if (videoElement) {
        const source = videoElement.querySelector('source');
        const newUrl = `/api/video/${clipId}`;
        
        console.log(`Loading clip ${index + 1}/${playlist.length} for ${camera}: ${newUrl}`);
        
        if (source) {
            source.src = newUrl;
        } else {
            videoElement.src = newUrl;
        }
        
        videoElement.load();
    }
}

// Sync playback across all cameras
window.syncAllVideos = function(targetTime) {
    Object.keys(videoPlaylists).forEach(camera => {
        const videoElement = document.getElementById(`video-${camera}`);
        if (videoElement) {
            // Find which clip this time falls into
            // For now, just seek within current clip
            // TODO: Calculate total duration and find correct clip
            videoElement.currentTime = Math.min(targetTime, videoElement.duration);
        }
    });
};
