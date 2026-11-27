# CFLbidtools

## Version log

Open **版本记录** in the workbench header to see the current application version,
build number, release notes, and the recent committed changes included at build
time. The log is bundled with the app and does not need Git or a GitHub connection
at runtime. Builds without Git history still show the version and release notes.

For a release, update the version and increment the mobile build number in
`build/WorkbenchVersion.props`, then add a release note to [CHANGELOG.md](CHANGELOG.md).
Normal builds do not change those numbers. At build time the project records up
to 60 recent Git commit summaries and short IDs. Commit messages are the automatic
revision notes; uncommitted edits are not included in that list.

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

A cached browser window can open even when the selection service is stopped.
For the Windows installation, start **CFL Engineering Workbench** from the
Start menu; for a development copy, restart the project at its original address.
Use **重试连接** to reload the pump catalog without reentering the duty.

The Visual Studio profile uses `https://localhost:65183/` and requires a valid,
trusted localhost certificate in the current user's Personal store. On a new
Windows development machine, run `dotnet dev-certs https --trust` before
starting the profile. The installed Windows workbench uses a different,
loopback-only HTTP address and does not use this profile.

## Pump data handoff

New ZTools projects start without sample equipment. For Z and CZ selections,
**按选型新建项目** creates a blank workbook and fills its first equipment row.
Choose the catalog model when a hydraulic size matches several ZTools models.
ZTools product prefixes ZA, ZE and ZF are supported for Z; CZ requires the
matching CZ model. The pump selection can also update an existing matching
equipment row. Existing projects are preserved when creating a new one.
Other pump families remain available for reference in ZTools.

On creation, the model goes in `input!N21`. The hydraulic fields written on
creation or update are:

| Column | Value |
| --- | --- |
| L | Rated capacity in m³/h |
| M | Requested differential head in m |
| P | Required NPSH in m |
| Q | Rated efficiency, stored as a fraction and displayed as a percentage |
| R | Rated pump speed in rpm |

Optional project and equipment details from **提交资料项目参数** transfer when
creating a project. The printed pump model and company name do not change the
catalog model or hydraulic data used for calculations.

**电机规格** shows the selected pump RPM and recommended motor kW as read-only.
The motor series, explosion marking, IP rating, manufacturer, nameplate
frequency and VFD or line operation are specified separately. The frame and
voltage still select the actual GA geometry; confirm them from the vendor.
When the selection has no supported motor recommendation, enter a confirmed
motor rating before calculation. Select the supported
API 610 edition (7, 8 or 10), bearing type, flushing plan and cooling plan under
**API 版本、轴承与方案**. These settings feed the existing ZTools calculations.
Available drawings depend on the data for the chosen pump and configuration:
for example, ZE uses edition-specific data and SEC templates, while CZ uses
its own bearing-based SEC templates. Recalculate before generating GA, SEC
and PID. The pump model and hydraulic fields can be edited on the page.
Use **导出 Excel** when an Excel copy is needed. The workbook still contains
only the `input` sheet. The
source workbooks and DWG templates retain their original formats.

## PDFs and phone installation

Enter **项目基本信息** above the pump duty controls; the same details pass to ZTools.
**导出 PDF** saves the selected curve with its three plots and two-column selection
data sheet on A4. The Print button remains available. ZTools exports GA, PID and SEC
PDFs with a centered grid of small CFL logos tilted 45 degrees behind the vector
drawing on each page. The curve and drawing PDF watermarks use about 8% opacity.
The CAD export
continues to generate editable DWG files from the original templates.

In Chrome or Edge on localhost or HTTPS, choose **选择项目目录** on the selection
page or **选择工作目录** in ZTools. For a single equipment item, Curve, GA, SEC and
PID PDFs go into the same folder. Use **项目设备项数** for a multi-item project so
the four categories go into separate subfolders from the first export. Added
ZTools equipment rows also trigger category folders for subsequent exports.
Existing files are not moved or overwritten. Other browsers can download
PDFs individually using the same filenames.

The manifest, icons and service worker prepare the workbench for home screen
installation. A phone needs a reachable HTTPS deployment to use the server.
The cached page does not perform calculations or generate files offline.
Use **添加设备项** to queue each pump duty and **批量选型** to find a result per item.
Open a queued item, preview a result, then click **确认选型** to save that pump
and its duty to the item. Previewing another result does not overwrite it.
Efficiency and NPSHr can be adjusted on a selected result. Use **应用修正**
to update its curves and report, or **恢复计算值** to restore the catalog
calculation. Confirm the batch row again to retain the adjustment. The catalog
and search ranking are unchanged.
**批量导出曲线** saves the
selected curves; **批量提交资料** starts a single ZTools project with all mapped
items. Use **批量图纸参数** to edit motor, bearing, plan, API edition and detail
inputs, then calculate and generate all checked items. ZTools drawing packages
require a matching ZA/ZE/ZF/CZ model for every item. Other families can export
curve PDFs.

## Liquid viscosity

Enter dynamic viscosity (cP) at the pumping temperature. Leaving it blank
keeps the original water curves. The engine converts the entry to cSt using
the entered density and applies ANSI/HI 9.6.7-2021 corrections per hydraulic
stage for Newtonian liquids. The correction affects candidate selection,
curve plots, efficiency, shaft power and the existing motor sizing rule.
The PDF records the viscosity and correction factors. NPSHr is estimated
using the conservative side-inlet coefficient and needs manufacturer
verification. The engine does not extrapolate past 3,000 cSt or the method's
specific-speed or B-factor limits.

## Stages and clearance

Leave the stage count blank to search the applicable HydCalc catalog range.
An entered count is an exact filter. Limits come from the pump size and
speed band in `PumpProperties.txt`; fixed-stage PDF curves use the count
printed on the source sheet. The result and curve report show the applicable range.

Standard and API are clearance variants, independent of the API 610 pump
classification. The selector uses the matching linked HydCalc curve and
evaluates its own head, efficiency and NPSHr data. It neither derives API
curves from Standard curves nor applies a generic trim or power penalty.
Unspecified clearances are eligible only when no clearance is requested.
The supplied catalog has no selectable Hot curve.

MSD is the single-suction series. MSD2 groups double-suction MSD variants,
including the source families MSD-D and MSD-DD, without changing the hydraulic
data or curve numbers.

## Curve numbers

The curve catalog and selected-pump view show the new and original curve
numbers from `CFL_Curve_Library_MOD.xlsx`. Search the catalog by either
number. The curve PDF and print view show only the new number. The number
mapping is bundled with the app and does not change the hydraulic data.

The same workbook's `new 系列` column sets the series shown in selection
and curve PDF exports. BKC, CAP8, HZZ, and MSE are marked `NONE`: they
are excluded from selection and remain in the library under their
original series names. The source family still identifies the hydraulic
and ZTools records (for example, X uses Z and XC uses CZ).

Choose X to specify XA, XE, XF, XZX or XGP. These subtypes share the X
hydraulics; the subtype changes the name on selection results, plots
and curve PDFs. Leaving it unset keeps the generic X name. ZTools
drawing-model selection works as before.

X-series selection applies the minimum stable flow at the rated speed and
trimmed impeller diameter, using the same estimate as the MCSF line on the plot.
The automatic selector retains a 10% margin over that estimated flow.

The 51 HDD and KBB drawings are grouped under `XDB` in selection and the curve
library. The edited BB2 register supplies the curve number prefix, with BB2
serials 267 through 317 appended in register order. Pump sizes, previous curve
IDs, source PDF filenames, original references, and review notes are retained.
The 36 selectable curves use the plotted metric traces and published RPM and
stage count. The selector interpolates at the same flow between available trims,
without speed conversion or curve extrapolation. It uses plotted efficiency and
shaft power separately, and checks that they agree at the duty point. The shared
NPSHr trace is provisional for the selected impeller. Curves with incomplete
traces, paired diameters, conflicting stages, or inconsistent source performance
remain visible for review, but do not produce selection results.

To regenerate the BB2 resource, run `scripts/import-bb2-catalog.py` with the
original curve-number workbook and the digitized `BB2_curves.json` export to
produce a staging catalog. Then run `scripts/renumber-bb2-catalog.py` with the
edited BB2 register, staging catalog, bundled `CurveNumbers.json`, and
`vendor/PumpSelectionTools/Resources/Hydcalc/BB2Curves.json` as the output.

### BB3 and BB4 drawing registers

The BB3 SCSK drawings are under XDA; the original MSD and MSD2 series are
unchanged. Each BB4 LO drawing has one hydraulic record and one curve number.
XMC and XMD use it under BB4. XMR can also select and rate it under BB5,
with the result, plots and PDF marked as shared BB4 hydraulics for preliminary
selection. The native XMR curves are unchanged. Final XMR performance and
mechanical limits must be confirmed for the actual pump by the manufacturer.
The numeric suffix continues the existing sequence for
each API type, in register row order. The library keeps the original drawing
reference, filename, impeller traces, and any unresolved data issues.

The preliminary selector uses the published RPM and maximum-impeller data
only. It enforces the marked minimum flow, available trace domains, NPSH margin,
and allowed stage counts. Head and shaft power scale with the number of
identical stages; NPSHr does not. BB4 uses the source's 3–15 stage range plus
any explicitly listed exceptions. Unknown stage basis, conflicting identities,
or flagged engineering data remain in the library but cannot be selected.
The source does not provide a validated lower-trim, clearance, or viscosity model.

Build the bundled resource with `scripts/import-bb34-registers.py`, passing
the directory containing the BB3 and BB4 register/extraction folders, the
bundled `CurveNumbers.json`, and `BB34Curves.json.gz` as the output.

### SM (XDC / BB1)

The SM scan adds 57 size/speed sheets to the XDC curve library, without replacing
any SMN curves. New numbers use `SM_<size>_<serial>`, continuing the XDC
sequence at 619. The printed curve number and page are retained. Nine sheets
are enabled for preliminary selection; the rest are indexed for further review.

SM is horizontal, single-stage and double-suction. Selection is limited to the
printed RPM and the available digitized data. The traces use the metric axes
(m3/h, m, kW) and linear interpolation between published diameters. Efficiency
is estimated from head and shaft power; NPSHr at intermediate diameters is
interpolated only where both extreme-diameter traces cover the requested flow.
The scan does not specify clearance. Verify the rating with the manufacturer
before final selection.

The scan PDF is not bundled. To regenerate the resource, run
`scripts/import-sm-curves.py` with the SM PDF, the bundled `CurveNumbers.json`,
and `vendor/PumpSelectionTools/Resources/Hydcalc/SMCurves.json` as output.
`--overlay-dir` generates review images showing the sampled curves on each scan.

AHLSTAR A 23-40 uses the digitized K50551 head and shaft-power traces from
page 21 of `ahlstar_performance_curves.pdf` (2950 rpm) for normal selection
at 2980 rpm. The app scales for speed and interpolates between adjacent
published diameters (210, 245, 280, 315 and 330 mm) where both traces cover
the duty flow. Efficiency is calculated from head and shaft power. At
30 m³/h and 150 m, the estimated trim is 308.79 mm.

The original 40 points remain in the catalog. Their 330 mm NPSHr values
supplement the digitized chart where that trace is unclear. NPSHr between
published traces is interpolated and marked as an estimate. Selection
does not extend the curves beyond their digitized flow ranges or apply an
unverified viscosity correction. The selection result and curve PDF flag
estimates for manufacturer confirmation. The normal selection screen
shows only the selected curve; the trim data remain available to the
application's internal routes.

The AHLSTAR source index covers 3,766 pump-curve pages in the 3,991-page
catalog. It preserves each page's type, size, published curve number and
available speed and diameter metadata. Pages for range charts, indexes
and sealing equipment are not individual pump curves. The library
distinguishes source-only pages from selection data and preserves
separate metric/US chart pages even when their curve numbers match.

Numerical selection includes K50551, the four individually digitized
charts, and 867 bulk-extracted fixed-speed vector chart pages. The new
pages contain 3,292 paired head/power diameter traces and 1,746 NPSHr
traces. The extractor checks the printed axes, diameter labels and common
head, power and suction ranges before accepting a chart. Selection
interpolates NPSHr only between documented traces over an overlapping
flow range; it does not extrapolate. Variable-speed charts and uncertain
matches are still searchable but are not used for selection.

The raster import attempted 1,032 image-based pages using local Windows
OCR and separate line tracing. It enabled 57 A-series pages (31 metric
and 26 US-unit charts), with 274 paired head/power diameter traces and
142 NPSHr traces. Both printed flow axes must agree, and a second OCR
pass checks the head, power and NPSHr units. Intersecting or unmatched
sections are excluded from the selectable flow domain. The other 975
image pages remain in the source index, with their first rejection
reason recorded in the extraction audit.

The two reviewed 1470 rpm Open1 curves in `Ahlstar33-100.json` use separate
manufacturer sheets: A33-100 K18490 (page 56) and APP33-100 K13847 (page 1066).
K18490 uses the reviewed head-image digitization and the NPSHr screenshot,
with shaft power from page 56. K13847 uses the curves and right-hand NPSHr
scale on page 1066. APP's 330 mm head/power curves are the published values.
For preliminary selection, the missing 330 mm NPSHr curve uses the 340 mm
trace scaled with Q proportional to D and NPSHr proportional to D squared,
with the published 340 mm value at the same flow as a floor. This estimate
never extends beyond the overlapping flow ranges. Selection and reports
identify any NPSHr that uses this estimated trace. Verify those values with
the manufacturer before final selection. The APP sheet's shaft power excludes
degassing-system power. Run `scripts/import-ahlstar-33-100.py --help` for the
targeted build.

Run `scripts/import-ahlstar-index.py` and `scripts/import-ahlstar-traces.py`
with Python and `pypdfium2` to regenerate the embedded source index and
the individually calibrated traces from the supplied PDF. Run
`scripts/import-ahlstar-vector.py` to regenerate `AhlstarVector.json.gz`
and a page-by-page calibration and rejection audit.
`scripts/import-ahlstar-raster.py` uses `numpy`, `Pillow`, `pypdfium2`
and the Windows English OCR language pack. Run it first with the vector
audit to cache OCR labels, then with `--index` and `--output` to trace
the curves, and finally with `--candidates` to check the units and
produce `AhlstarRaster.json.gz`. The scripts support `--help`.

### Import the pump datasheet

Use **导入选型一览表** in the batch selection panel for the fixed CFL supply-list
template. Keep its headers in rows 7–10 and fill one equipment item per row
above 配置说明. The workbook remains unchanged. Selectable duties use H (m³/h),
I (m), J (normal °C), L (mPa·s = cP), M (kg/m³), and P (NPSHa, assumed m;
check the unit). Missing density uses 1000 kg/m³ and the starting frequency is
50 Hz, both editable. Q (quoted NPSHr) and the remaining proposal specifications
are reference notes; they do not replace computed hydraulic results or ZTools
equipment settings.

Review an imported row in the selection form. Use **更新设备项** to save just
the inputs for batch selection, or run **开始选型** and click **确认选型** to
save a specific match. Reviewed Excel messages remain under **原表参数**.
Run **批量选型** to match the remaining duties, then choose the items and matching models
for ZTools. In **批量图纸参数**, compare the proposal notes with the actual motor,
API version, bearing, seal/flush/cooling and material settings. Confirm each
imported device, then calculate and generate GA, SEC, and PID as a batch. Later
edits on an imported item reopen its confirmation. The existing ZTools
catalog limit remains in effect (only supported Z and CZ models).

## Deployment packages

On Windows, **Build > Rebuild Solution** in Visual Studio generates both the
x64 Windows setup and the Android test APK in `artifacts/packages`. A regular
Build or Run does not generate installers. The packages use Release binaries
regardless of the active solution configuration. Packaging errors are shown
in the build output and fail the rebuild. For a rebuild without installers,
pass `-p:CflPackageOnRebuild=false` to MSBuild.

To package separately, run `deploy/Build-Packages.ps1` in PowerShell with
`-Platform Windows` or `-Platform Android`. The Windows setup uses Inno Setup,
installs per user, and opens the workbench on loopback. Existing local project data is separate.
Use an appropriately licensed Inno Setup compiler for commercial distribution.

Use `-Platform Android` with the Android SDK and JDK to build the test-signed
APK. Android opens the bundled workbench by default: pump selection, the
curve catalog, and supported ZTools calculations and drawing exports run on
the device without a PC or network connection. The Android file picker saves
PDF, DWG, Excel and batch ZIP exports. Workbooks created in the Android app
are stored on the phone; they aren't synced to the PC. **Connect to PC** in
the Android menu optionally opens a separate online session in the browser.
The iPhone client still uses the online workbench.

### Connect a phone to the Windows installation

1. Start the installed **CFL Engineering Workbench** on the PC.
2. Install Tailscale on the PC and phone and connect both to the same approved
   private network. MagicDNS and HTTPS must be enabled there. Limit access to
   this PC's port 9443 to approved users in the network's access rules.
3. Open **CFL Workbench - Phone Connection** from the Windows Start menu.
   Enable private phone access, then send the displayed HTTPS link to the phone.
   Use that link in the Android app or directly in the phone's browser.
4. On iPhone, open the link in Safari and select **Add to Home Screen** and
   **Open as Web App**. No IPA is needed for this option.

Do not use `localhost` or a PC-only HTTP address on the phone. Keep the
Windows workbench running while using the phone. The setup uses Tailscale
Serve, not Funnel, and does not open the app to the public. The same shortcut
can turn off phone access; turn it off before uninstalling. Workbooks already
open in a PC browser aren't automatically synced to a separate phone session.
For a separately hosted deployment, supply your own authenticated HTTPS link.

On a Mac with PowerShell, Xcode and the .NET iOS workload, set the environment
variables `CFL_APPLE_SIGNING_KEY` and `CFL_APPLE_PROVISION` before rebuilding
the solution to generate the signed IPA in `artifacts/packages/ios`. For
manual packaging use `-Platform iOS` with `-AppleSigningKey` and
`-AppleProvision`. Windows does not build the IPA. Store distribution requires
the appropriate release signing credentials.

## Source and verification

`vendor/SDOCtools` is based on [SDOCtools commit 6b35c6a](https://github.com/paciguard/SDOCtools/commit/6b35c6af366f02bcc8ba24b1ad8226a2d09c8440).
It contains the portable engine, templates, and drawing viewer used by this
workbench. The web adaptations are confined to this copy. The pump module
uses the desktop hydraulic and selection source with a portable workbook reader.

`browser-smoke.mjs` exercises both modules, the transfer checks, and
phone-sized layouts against the loopback preview. It requires the Playwright
Node package and Microsoft Edge. Set `CFL_DATA_DIR` to an isolated test folder
before running it.

Run `dotnet run --project deploy/OfflineCheck -- artifacts/offline-check-data`
to exercise the on-device calculation and export routes without starting a
server. The `offline-browser-smoke.mjs` test uses the same engine through a
simulated Android bridge with browser networking disabled. Set
`CFL_OFFLINE_TEST_DLL` to the built `OfflineCheck.dll` before running it.
