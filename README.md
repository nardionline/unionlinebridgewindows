# Unionline Print Bridge (Windows)

Windows desktop agent for local label printing through an installed Windows printer driver. The project is configured for GitHub Actions so the EXE can be built online.

## Build online with GitHub Actions

1. Create a new GitHub repository, for example `UnionlinePrintBridge`.
2. Upload the contents of this folder to the repository root (the `.github` folder must also be uploaded).
3. Open **Actions** → **Build Unionline Print Bridge** → **Run workflow**.
4. When complete, open the workflow run and download the artifact `UnionlinePrintBridge-win-x64`.
5. Extract and run `UnionlinePrintBridge.exe` on Windows 10.

The workflow also runs automatically after pushes to `main` that change source or project files.

## First run

1. Install the correct Windows driver for the label printer.
2. Start the Bridge and select the installed printer.
3. Set label width and height (default 40 × 30 mm).
4. Set the physical gap value for reference. The actual gap/sensor calibration is controlled by the printer's driver/Printing Preferences and printer media calibration; it is not universally configurable through Windows PrintDocument.
5. Generate/copy the local key and keep it private.
6. Click **Printo etiketë test**. Confirm the driver accepts the custom paper size and calibrate the stock in Printing Preferences if necessary.
7. Click **Nis shërbimin lokal** to accept print requests from the Unionline website.

## Local HTTP endpoint

- URL: `http://127.0.0.1:8765/print`
- Method: `POST`
- Header: `X-Unionline-Key: <key shown in Bridge>`
- Content-Type: `application/json`
- Allowed web origins: `https://ebaumarket.com`, `https://www.ebaumarket.com`

Example body:

```json
{
  "items": [
    {
      "name": "Trapano 750W Professional",
      "price": "4,990 L",
      "code": "TR-10025",
      "barcode": "123456789012",
      "quantity": 2
    }
  ]
}
```

The local endpoint binds only to `127.0.0.1`; it is not exposed on the LAN or internet. Do not publish the local key. The Bridge currently accepts and queues the request to the Windows print spooler; the response means accepted, not proof that physical labels have emerged.

## Important current limitations

- The first build is a functional scaffold for printer selection, custom label dimensions, a test label, and a local authenticated print endpoint.
- Generic Windows drivers differ. Some ignore custom `PaperSize` values unless the matching stock size is created/selected in Printing Preferences.
- Gap calibration is printer/driver-specific. Configure label sensing and gap in the printer's driver; the numeric Gap field is currently informational.
- Barcode rendering is not yet implemented in this first scaffold: the barcode value is currently printed as text. Do not use this build for production barcode labels until barcode graphics and a physical test are completed.
- The web module still needs its matching JavaScript change to POST the selected print list to this endpoint and a safe way to provision the local key to each workstation. That integration is a separate next step.
