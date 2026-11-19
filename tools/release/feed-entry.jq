# feed-entry.jq: puts one version entry first in a Jellyfin feed (manifest.json's shape). Shared by release.yml and
# test-build.yml, so both feeds' entries have the same fields; cut.ts reads the commit back out of `changelog`.
#   jq --arg v <version> --arg abi <targetAbi> --arg url <zip url> --arg md5 <checksum> --arg log <changelog url> \
#      --argjson keep <true|false> -f tools/release/feed-entry.jq manifest.json
# keep: true keeps the older versions (the public feed); false leaves this one alone (the test feed).
# Any entry already there for this version is replaced, so a re-run never lists a version twice.
.[0].versions = [{version: $v, changelog: $log, targetAbi: $abi, sourceUrl: $url, checksum: $md5, timestamp: (now | todate)}]
  + (if $keep then .[0].versions | map(select(.version != $v)) else [] end)
