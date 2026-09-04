window.teslaCamPlayer = (function () {
    const SKIP_SECONDS = 5;
    let timelineTimer = null;
    let timelineRef = null;
    let timelineBusy = false;
    let maxDuration = 60;
    let keysBound = false;

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

    function notifySeek(time) {
        if (!timelineRef) return;
        timelineRef.invokeMethodAsync("OnPlayerSeek", time).catch(() => { });
    }

    function skipBy(delta, duration) {
        const limit = Number(duration) > 0 ? Number(duration) : maxDuration;
        const next = Math.max(0, Math.min(limit, getTimelineTime() + Number(delta)));
        seek(next);
        notifySeek(next);
        return next;
    }

    function videoByCamera(cameraName) {
        if (cameraName) {
            const named = document.querySelector(`.camera-video[data-camera="${cameraName}"]`);
            if (named) return named;
        }
        return document.querySelector(".camera-video.event-camera") || videos()[0] || null;
    }

    function requestFs(el) {
        if (!el) return;
        if (el.requestFullscreen) return el.requestFullscreen();
        if (el.webkitRequestFullscreen) return el.webkitRequestFullscreen();
        if (el.webkitEnterFullscreen) return el.webkitEnterFullscreen();
    }

    function exitFs() {
        if (document.exitFullscreen) return document.exitFullscreen();
        if (document.webkitExitFullscreen) return document.webkitExitFullscreen();
    }

    function isFullscreen() {
        return !!(document.fullscreenElement || document.webkitFullscreenElement);
    }

    function fullscreen(cameraName) {
        const video = videoByCamera(cameraName);
        if (!video) return;

        if (isFullscreen()) {
            const current = document.fullscreenElement || document.webkitFullscreenElement;
            if (!cameraName || current === video) {
                exitFs();
                return;
            }
        }

        requestFs(video);
    }

    function isTypingTarget(el) {
        if (!el) return false;
        const node = el.nodeType === 3 ? el.parentElement : el;
        if (!node || !node.closest) return false;
        const tag = (node.tagName || "").toLowerCase();
        if (tag === "input" || tag === "textarea" || tag === "select") return true;
        if (node.isContentEditable) return true;
        return !!node.closest('[contenteditable="true"], .mud-input, .mud-picker, .mud-overlay, .mud-popover, .mud-menu, .mud-select');
    }

    function onKeyDown(e) {
        if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
        if (isTypingTarget(e.target)) return;
        if (videos().length === 0) return;

        const key = e.key;
        if (key === " " || key === "Spacebar") {
            e.preventDefault();
            if (timelineRef) {
                timelineRef.invokeMethodAsync("OnPlayerTogglePlayPause").catch(() => { });
            }
            return;
        }
        if (key === "ArrowLeft") {
            e.preventDefault();
            skipBy(-SKIP_SECONDS);
            return;
        }
        if (key === "ArrowRight") {
            e.preventDefault();
            skipBy(SKIP_SECONDS);
            return;
        }
        if (key === "f" || key === "F") {
            e.preventDefault();
            fullscreen();
        }
    }

    function onDblClick(e) {
        const video = e.target && e.target.closest && e.target.closest(".camera-video");
        if (!video) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        fullscreen(video.dataset.camera);
    }

    function bindKeys() {
        if (keysBound) return;
        document.addEventListener("keydown", onKeyDown, true);
        document.addEventListener("dblclick", onDblClick, true);
        keysBound = true;
    }

    function unbindKeys() {
        if (!keysBound) return;
        document.removeEventListener("keydown", onKeyDown, true);
        document.removeEventListener("dblclick", onDblClick, true);
        keysBound = false;
    }

    function startTimeline(dotNetRef, intervalMs, duration) {
        stopTimeline();
        timelineRef = dotNetRef;
        if (Number(duration) > 0) {
            maxDuration = Number(duration);
        }
        bindKeys();
        const ms = Number(intervalMs) > 0 ? Number(intervalMs) : 250;
        timelineTimer = setInterval(() => {
            if (timelineBusy || !timelineRef) return;
            timelineBusy = true;
            timelineRef.invokeMethodAsync("OnTimelineTick", getTimelineTime())
                .catch(() => { })
                .finally(() => { timelineBusy = false; });
        }, ms);
    }

    function stopTimeline() {
        if (timelineTimer) {
            clearInterval(timelineTimer);
            timelineTimer = null;
        }
        timelineBusy = false;
    }

    function dispose() {
        stopTimeline();
        unbindKeys();
        timelineRef = null;
    }

    return {
        seek,
        play,
        pause,
        reset,
        reload,
        skipBy,
        fullscreen,
        getTimelineTime,
        startTimeline,
        stopTimeline,
        dispose
    };
})();
