// Hands a file the server generated to the browser as a download.
//
// A blob and an object URL rather than a data: URI. A player export grows with
// every session recorded, and data: URIs hit a length ceiling that varies by
// browser — so the version that works in testing on a small database is exactly
// the one that fails later on a big one, which is the worst way to find out.
//
// Base64 in, because the payload crosses the SignalR circuit as a string and
// arbitrary text would need escaping this avoids entirely.
window.bedrockSentryDownload = function (fileName, base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);

    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }

    const url = URL.createObjectURL(new Blob([bytes], { type: 'application/json' }));

    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);

    // Revoked on the next tick rather than immediately: Safari has been known to
    // cancel the download if the URL goes before it has started reading.
    setTimeout(() => URL.revokeObjectURL(url), 0);
};
