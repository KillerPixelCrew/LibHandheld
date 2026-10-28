// Generates the Atlas 8 front line art on the 2000 x 905 grid of the maintainer's reference photo.
import fs from "node:fs";

const out = process.argv[2];
const r = (v) => Math.round(v * 10) / 10;
const mirrorX = (x) => 2000 - x;

// Left half of the body outline, from the top centre round the left grip to the bottom centre.
const half = [
    ["M", 1000, 17],
    ["L", 335, 17],
    ["C", 290, 17, 268, 4, 205, 5],
    ["C", 150, 6, 104, 34, 90, 88],
    ["C", 58, 190, 22, 380, 22, 560],
    ["C", 22, 712, 44, 838, 118, 880],
    ["C", 170, 906, 298, 908, 362, 882],
    ["L", 1000, 880],
];
const pathOf = (cmds, mirror) =>
    cmds
        .map(([c, ...p]) => c + p.map((v, i) => r(i % 2 === 0 && mirror ? mirrorX(v) : v)).join(" "))
        .join("");

const circle = (cx, cy, rad) => `<circle cx="${r(cx)}" cy="${r(cy)}" r="${r(rad)}"/>`;
const line = (x1, y1, x2, y2) => `<path d="M${r(x1)} ${r(y1)}L${r(x2)} ${r(y2)}"/>`;
const rect = (x, y, w, h, rx) =>
    `<rect x="${r(x)}" y="${r(y)}" width="${r(w)}" height="${r(h)}" rx="${r(rx)}"/>`;

const parts = [];
// Body and shoulder bumpers.
parts.push(`<path d="${pathOf(half, false)}"/>`, `<path d="${pathOf(half, true)}"/>`);
parts.push(`<path d="M92 86C150 52 240 34 335 30"/>`, `<path d="M1908 86C1850 52 1760 34 1665 30"/>`);

// Screen bezel and display.
parts.push(rect(378, 22, 1244, 836, 14), rect(405, 55, 1190, 740, 4));

// Sticks: base ring, light ring and cap.
for (const [cx, cy] of [[185, 232], [1705, 395]]) {
    parts.push(circle(cx, cy, 80), circle(cx, cy, 62), circle(cx, cy, 48));
}

// D-pad disc with its four tick marks.
parts.push(circle(282, 408, 86), circle(282, 408, 74));
parts.push(line(282, 352, 282, 374), line(282, 442, 282, 464), line(222, 408, 244, 408), line(320, 408, 342, 408));

// Front OEM buttons: a recessed slot holding two pill buttons on each side.
for (const x of [319, 1628]) {
    parts.push(rect(x, 70, 40, 210, 20), rect(x + 3, 76, 34, 92, 17), rect(x + 3, 182, 34, 92, 17));
}
// Their prints, small: Task key and View on the left, Predator and Menu on the right.
parts.push(`<path d="M331 128 335 118H345L341 128Z"/>`);
parts.push(rect(330, 223, 11, 11, 1.5), rect(335, 228, 11, 11, 1.5));
parts.push(`<path d="M1640 117 1644 125 1648 121V131M1656 117 1652 125 1648 121"/>`);
parts.push(line(1641, 223, 1655, 223), line(1641, 228, 1655, 228), line(1641, 233, 1655, 233));

// Face buttons with their letters.
const face = { Y: [1797, 158], X: [1733, 226], B: [1863, 226], A: [1797, 294] };
const letters = {
    Y: "M-9 -12L0 0L9 -12M0 0V12",
    X: "M-9 -12L9 12M9 -12L-9 12",
    A: "M-10 12L0 -12L10 12M-6 4H6",
    B: "M-7 -12V12H2A6 6 0 0 0 2 0H-7M-7 -12H1A6 6 0 0 1 1 0",
};
for (const [name, [cx, cy]] of Object.entries(face)) {
    parts.push(circle(cx, cy, 34), `<path transform="translate(${cx} ${cy})" d="${letters[name]}"/>`);
}

// Speaker grilles: nine slanted slots each, stacked up and to the right.
for (const x0 of [250, 1674]) {
    for (let i = 0; i < 9; i++) {
        const sx = x0 + i * 6;
        const sy = 828 - i * 6.5;
        parts.push(line(sx, sy - 56, sx + 26, sy));
    }
}

const svg =
    `<svg width="66mm" height="29.865mm" version="1.1" viewBox="0 0 2000 905" xmlns="http://www.w3.org/2000/svg">` +
    `<g fill="none" stroke="#889199" stroke-width="3.6" stroke-linecap="round" stroke-linejoin="round">` +
    parts.join("") +
    `</g></svg>\n`;
fs.writeFileSync(out, svg);
console.log(out, svg.length, "bytes");
