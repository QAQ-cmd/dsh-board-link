window.__ModuleLoader__.load({
  id: "dsh-board-link",
  factory: (require) => {
    var module = { exports: {} };
    var exports = module.exports;
    Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
    let react = require("react");

    const PANEL_ID = "board-link";

    function BoardIcon({ size = 18 }) {
      return react.createElement("svg", {
        width: size,
        height: size,
        viewBox: "0 0 24 24",
        fill: "none",
        stroke: "currentColor",
        strokeWidth: 1.8,
        strokeLinecap: "round",
        strokeLinejoin: "round",
        "aria-hidden": "true",
      },
        react.createElement("rect", { x: 3, y: 4, width: 18, height: 14, rx: 2 }),
        react.createElement("path", { d: "M8 22h8M12 18v4" }),
        react.createElement("path", { d: "M7 8h7M7 12h5" })
      );
    }

    function BoardPage() {
      return react.createElement("div", {
        style: {
          display: "flex",
          flexDirection: "column",
          height: "100%",
          minHeight: 0,
          background: "var(--dsw-alias-bg-base, #fff)",
        },
      },
        react.createElement("iframe", {
          title: "DSH Link 板端控制台",
          src: "/board-link/ui",
          style: {
            flex: "1 1 auto",
            width: "100%",
            height: "100%",
            minHeight: 0,
            border: 0,
            background: "#fff",
          },
        })
      );
    }

    function apply(ctx) {
      ctx.slots.inject("main", () => ctx.slots.register({
        name: "main",
        key: PANEL_ID,
      }, BoardPage));

      ctx.slots.inject("sidebar.panellist", () => ctx.slots.register({
        name: "sidebar.panellist",
        id: PANEL_ID,
        order: 40,
        label: () => "板端控制台",
      }, BoardIcon));
    }

    const inject = ["slots"];
    exports.apply = apply;
    exports.inject = inject;
    return module.exports;
  },
});
