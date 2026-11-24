// Regenerate after changing Assets/flags. Requires Node and @resvg/resvg-js 2.6.2.
const fs = require("node:fs");
const path = require("node:path");
const { Resvg } = require("@resvg/resvg-js");

const assets = path.join(__dirname, "../src/IracingOverlay.App/Assets");
const flags = path.join(assets, "flags");
const width = 96, height = 72, columns = 16;
const regions = new Intl.DisplayNames("en", { type: "region" });
const codes = fs.readdirSync(flags).filter(file => file.endsWith(".svg")).sort()
    .map(file => path.basename(file, ".svg"));
const tiles = codes.map((code, index) => {
    const source = fs.readFileSync(path.join(flags, code + ".svg"));
    const png = new Resvg(source, { fitTo: { mode: "width", value: width } }).render().asPng();
    const x = (index % columns) * width, y = Math.floor(index / columns) * height;
    return `<image x="${x}" y="${y}" width="${width}" height="${height}" xlink:href="data:image/png;base64,${png.toString("base64")}"/>`;
});
const svg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"
    width="${columns * width}" height="${Math.ceil(codes.length / columns) * height}">${tiles.join("")}</svg>`;
fs.writeFileSync(path.join(assets, "flag-atlas.png"), new Resvg(svg).render().asPng());
const entries = codes.map(code => ({ code, name: /^[a-z]{2}$/.test(code) ? regions.of(code.toUpperCase()) : code }));
fs.writeFileSync(path.join(assets, "flag-atlas.json"), JSON.stringify({ width, height, columns, entries }, null, 2) + "\n");
