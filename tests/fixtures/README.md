# External DXF regression fixtures

The files `cadcraft-sample-bracket.dxf` and `cadcraft-sample-floorplan.dxf` are copied from the CADCraft Corpus at https://github.com/storytold/cadcraft-corpus, pinned at upstream commit `fde57bc9e8c71c7c2d33a410edafa1afe44cae24`. Original paths: `cadcraft/sample-bracket.dxf` and `cadcraft/sample-floorplan.dxf`.

Copyright CADCraft contributors. Dual licensed MIT OR Apache-2.0, used here under MIT; see `CADCRAFT-LICENSE-MIT`. The corpus `LICENSES.tsv` identifies these as CADCraft-generated sample drawings. They are *not* verified industrial production drawings.

Purpose: confirm that imported real-file DXF headers/codepages are read and that unsupported units/entities reliably block manufacturing preflight. The files must not be used to claim industrial geometry validation or machine readiness.
