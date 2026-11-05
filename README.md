# Unionline Print Bridge Windows v1.2.1
- Rregullim i layout-it: e gjithë etiketa vizatohet fillimisht në një bitmap me rezolucion 203 DPI dhe pastaj vendoset në një faqe fizike të vetme sipas `PageBounds`.
- Test label: Unionline Test / 4,990 L / TR-10025 / barkod grafik Code 128.
- Layout 40 × 30 mm: titull majtas, vijë ndarëse, çmim djathtas, kod dhe barkod.
- Endpoint lokal pa çelës: `127.0.0.1:8765/status` dhe `/print`.
- Lejon Origin vetëm `https://ebaumarket.com` dhe `https://www.ebaumarket.com`.
- Driver-i duhet të ketë formatin fizik 40 × 30 mm dhe GAP-in të kalibruar te Printing Preferences. Nëse driver-i përdor format tjetër, përmbajtja mund të pozicionohet gabim.
## Build
Ngarko përmbajtjen e ZIP-it në repository GitHub; Actions → Build Unionline Print Bridge → Run workflow.
Artifact: `UnionlinePrintBridge-win-x64`.
## Shembull POST
`{"items":[{"name":"Trapano 750W","price":"4,990 L","code":"TR-10025","barcode":"123456789012","quantity":1}]}`
Shënim: CORS/Origin nuk është autentikim i fortë kundër programeve të tjera lokale. `labels.php` duhet të dërgojë POST te endpoint-i lokal; kjo ZIP përditëson vetëm Bridge-in.
