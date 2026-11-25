"""Export only YouTube/Google cookies from an existing browser profile for FluxTube."""
import sys
from http.cookiejar import MozillaCookieJar

from yt_dlp.cookies import extract_cookies_from_browser


def allowed(domain):
    domain = domain.lstrip(".").lower()
    return domain in ("youtube.com", "google.com") or domain.endswith((".youtube.com", ".google.com"))


browser, destination = sys.argv[1:3]
source = extract_cookies_from_browser(browser)
export = MozillaCookieJar(destination)
for cookie in source:
    if allowed(cookie.domain):
        export.set_cookie(cookie)

if not any(c.name in ("SAPISID", "__Secure-3PAPISID") and c.domain.lstrip(".").endswith("youtube.com") for c in export):
    print("No YouTube sign-in cookies found", file=sys.stderr)
    sys.exit(2)

export.save(ignore_discard=True, ignore_expires=True)
print("YouTube cookies exported")
