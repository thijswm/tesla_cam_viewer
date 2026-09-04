window.teslaCamPlayer = (function () {
    function videos() {
        return Array.from(document.querySelectorAll(".camera-video"));
    }

    function offsetOf(video) {
        const n = parseFloat(video.dataset.offset);
        return Number.isFinite(n) ? n : 0;
    }

    function applySeek(video, timelineSeconds) {
        const target = Math.max(0, Number(timelineSeconds) - offsetOf(video));
        video.dataset.seekTo = String(target);

        const set = () => {
            const seekTo = parseFloat(video.dataset.seekTo);
            if (!Number.isFinite(seekTo)) return;
            const duration = Number.isFinite(video.duration) ? video.duration : seekTo;
            video.currentTime = Math.min(seekTo, Math.max(0, duration));
        };

        if (video.readyState >= 1) {
            set();
        } else {
            video.addEventListener("loadedmetadata", set, { once: true });
        }
    }

    function seek(timelineSeconds) {
        videos().forEach((video) => applySeek(video, timelineSeconds));
    }

    function play() {
        videos().forEach((video) => {
            const pending = video.play();
            if (pending && typeof pending.catch === "function") {
                pending.catch(() => { });
            }
        });
    }

    function pause() {
        videos().forEach((video) => video.pause());
    }

    function reset() {
        videos().forEach((video) => {
            video.pause();
            try {
                video.currentTime = 0;
            } catch (_) { }
        });
    }

    function reload() {
        videos().forEach((video) => video.load());
    }

    function getTimelineTime() {
        let latest = 0;
        videos().forEach((video) => {
            if (!Number.isFinite(video.currentTime)) return;
            latest = Math.max(latest, video.currentTime + offsetOf(video));
        });
        return latest;
    }

    return { seek, play, pause, reset, reload, getTimelineTime };
})();
