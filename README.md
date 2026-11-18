# CFLbidtools

The browser workbench combines Pump Selection Tools with the SDOCtools
submission module. From the pump selection screen, **提交资料** opens ZTools
with the current result available for transfer.

## Run locally

Open `CFLbidtools.sln` in Visual Studio 2022 with the ASP.NET and web development
workload and the .NET 8 SDK. Set `CflWorkbench` as the startup project.
The solution also includes the pump selection and SDOCtools engines.

To run from this directory:

```powershell
dotnet run --project CflWorkbench.csproj --urls http://127.0.0.1:5178
```

Project workbooks are stored in the host user's local application data under
`CFL/WebWorkbench`. Set `CFL_DATA_DIR` to use a different directory. Use a
separate folder for test data. Data is separated by a browser cookie. This local
preview has no user sign-in and should stay on loopback.

## Pump data handoff

The workbench calculates the result again on the server. Z and CZ selections
can transfer to an existing equipment row when the hydraulic size matches its
ZTools model and the model, edition, baseplate and voltage resolve against the
embedded database. ZTools product prefixes ZA, ZE and ZF are supported for Z;
CZ requires the matching CZ model. Other pump families retain full pump
selection and are shown for reference in ZTools until their data is available.

Only these `input` worksheet fields are written:

| Column | Value |
| --- | --- |
| L | Rated capacity in m³/h |
| M | Requested differential head in m |
| P | Required NPSH in m |
| Q | Rated efficiency, stored as a fraction and displayed as a percentage |
| R | Rated pump speed in rpm |

Use the ZTools dialogs to choose the equipment model, materials, motor,
baseplate, accessories, drawing types and print options. Recalculate after a
transfer. The input workbook still contains only the `input` sheet. The
source workbooks and DWG templates retain their original formats.

## PDFs and phone installation

The pump report uses the browser print dialog. ZTools exports GA, PID and SEC
PDFs with a centered grid of small CFL logos tilted 45 degrees behind the vector
drawing on each page. The logo uses a 13% opacity PNG embedded in the server assembly. The CAD export
continues to generate editable DWG files from the original templates.

The manifest, icons and service worker prepare the workbench for home screen
installation. A phone needs a reachable HTTPS deployment to use the server.
The cached page does not perform calculations or generate files offline.
Android/iPhone installation and publishing are a later deployment step.

## Source and verification

`vendor/SDOCtools` is based on [SDOCtools commit 6b35c6a](https://github.com/paciguard/SDOCtools/commit/6b35c6af366f02bcc8ba24b1ad8226a2d09c8440).
It contains the portable engine, templates, and drawing viewer used by this
workbench. The web adaptations are confined to this copy. The pump module
uses the desktop hydraulic and selection source with a portable workbook reader.

`browser-smoke.mjs` exercises both modules, the transfer checks, and
phone-sized layouts against the loopback preview. It requires the Playwright
Node package and Microsoft Edge. Set `CFL_DATA_DIR` to an isolated test folder
before running it.
