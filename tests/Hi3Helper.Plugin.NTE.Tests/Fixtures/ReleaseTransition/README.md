# CN release transition, 2026-11-05

Public resource identities from the CN 1.4.18 and 1.5.3 manifests, with the 1.5.3 official diff list. No installed game content, credentials, absolute paths, or local timestamps are included.

- Current release configuration: `https://yhcdn2.wmupd.com/clientRes/PC_150/Version/Windows/config.xml`
- Manifest archive: `https://yhcdn2.wmupd.com/clientRes/PC_150/Version/Windows/version/1.5.3/ResList.bin.zip`
- Current `listHash`: `2242bb328fe627e1e0959f2ed67d66a9`
- Current `diffHash`: `f3effb028257b49ceb9f777afb9e0c3a`

The cache identity list reproduces a completed 1.5.0 predownload with seven small full resources already consumed by the old updater. The fixture assumes otherwise intact 1.4.18 files. It verifies that ordinary direct deltas require 11,923,529,736 bytes (11.10 GiB) and that nine cached preview deltas reduce remaining transfers to 1,421,258,914 bytes. A real partially updated installation can require fewer bytes. Cache hashes and patch output are checked separately by runtime and integration tests.
