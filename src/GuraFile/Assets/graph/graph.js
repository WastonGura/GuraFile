(function () {
  "use strict";

  let cy = null;
  let broadTagsVisible = false;
  const tooltip = document.getElementById("tooltip");

  const CategoryColors = {
    "图片": "#388E3C",
    "音频": "#F57C00",
    "视频": "#D32F2F",
    "文档": "#1976D2",
    "压缩包": "#7B1FA2",
    "代码": "#00796B",
    "其他": "#546E7A"
  };

  function sendToHost(type, payload) {
    if (window.chrome && window.chrome.webview) {
      try {
        window.chrome.webview.postMessage({
          type: type,
          version: "1.0",
          payload: payload || null
        });
      } catch (e) {
        console.error("Failed to post message to host:", e);
      }
    }
  }

  function getCategoryColor(category) {
    return CategoryColors[category] || CategoryColors["其他"];
  }

  function showTooltip(text, x, y) {
    if (!tooltip) return;
    tooltip.textContent = text;
    tooltip.style.left = (x + 12) + "px";
    tooltip.style.top = (y + 12) + "px";
    tooltip.classList.remove("hidden");
  }

  function hideTooltip() {
    if (!tooltip) return;
    tooltip.classList.add("hidden");
  }

  function initCytoscape() {
    cy = cytoscape({
      container: document.getElementById("cy"),
      elements: [],
      boxSelectionEnabled: false,
      autounselectify: false,
      style: [
        {
          selector: "node[nodeType = 'file']",
          style: {
            "label": "data(label)",
            "shape": "round-rectangle",
            "width": 32,
            "height": 32,
            "background-color": "data(color)",
            "color": "#333333",
            "font-size": 11,
            "text-valign": "bottom",
            "text-margin-y": 4,
            "text-max-width": 90,
            "text-wrap": "ellipsis",
            "border-width": 1,
            "border-color": "#ffffff"
          }
        },
        {
          selector: "node[nodeType = 'tag']",
          style: {
            "label": "data(label)",
            "shape": "ellipse",
            "width": "data(size)",
            "height": "data(size)",
            "background-color": "data(color)",
            "color": "#111111",
            "font-size": 11,
            "font-weight": "bold",
            "text-valign": "center",
            "text-halign": "center",
            "text-max-width": 100,
            "text-wrap": "ellipsis",
            "border-width": 2,
            "border-color": "#ffffff"
          }
        },
        {
          selector: "node:selected, node.selected",
          style: {
            "border-width": 3,
            "border-color": "#0078d4",
            "border-opacity": 1.0,
            "overlay-color": "#0078d4",
            "overlay-padding": 4,
            "overlay-opacity": 0.25
          }
        },
        {
          selector: "edge",
          style: {
            "width": 1.5,
            "line-color": "#90a4ae",
            "curve-style": "bezier",
            "opacity": 0.7
          }
        },
        {
          selector: "node:active",
          style: {
            "overlay-color": "#000000",
            "overlay-padding": 4,
            "overlay-opacity": 0.15
          }
        }
      ]
    });

    let lastTapNodeId = null;
    let lastTapTime = 0;
    const DOUBLE_TAP_THRESHOLD_MS = 350;

    cy.on("tap", "node", function (evt) {
      const node = evt.target;
      const now = performance.now();
      const isDoubleTap = (lastTapNodeId === node.id() && (now - lastTapTime) < DOUBLE_TAP_THRESHOLD_MS);

      lastTapNodeId = node.id();
      lastTapTime = now;

      cy.batch(() => {
        cy.nodes().unselect().removeClass("selected");
        node.select().addClass("selected");
      });

      const data = node.data();
      const payload = {
        nodeId: node.id(),
        kind: data.nodeType,
        fileId: data.fileId != null ? data.fileId : null,
        tagId: data.tagId != null ? data.tagId : null,
        label: data.label || ""
      };

      if (isDoubleTap) {
        lastTapNodeId = null;
        lastTapTime = 0;
        sendToHost("nodeActivated", payload);
      } else {
        sendToHost("nodeSelected", payload);
      }
    });

    cy.on("tap", function (evt) {
      if (evt.target === cy) {
        lastTapNodeId = null;
        lastTapTime = 0;
        hideTooltip();
      }
    });

    cy.on("mouseover", "node", function (evt) {
      const node = evt.target;
      const label = node.data("label") || "";
      const pos = evt.renderedPosition;
      showTooltip(label, pos.x, pos.y);
    });

    cy.on("mouseout", "node", function () {
      hideTooltip();
    });

    cy.on("pan zoom", function () {
      hideTooltip();
    });
  }

  function renderSnapshot(payload) {
    const startTime = performance.now();
    hideTooltip();

    if (!payload || !payload.files) {
      if (cy) cy.elements().remove();
      return;
    }

    const files = payload.files || [];
    const tags = payload.tags || [];
    const edges = payload.edges || [];

    // Calculate degrees for tag sizing
    const tagDegrees = {};
    for (const edge of edges) {
      tagDegrees[edge.target] = (tagDegrees[edge.target] || 0) + 1;
    }

    const elements = [];

    // Add file nodes
    for (const file of files) {
      elements.push({
        group: "nodes",
        data: {
          id: file.id,
          label: file.label,
          nodeType: "file",
          fileId: file.fileId,
          category: file.category || "其他",
          color: getCategoryColor(file.category)
        }
      });
    }

    // Add tag nodes
    for (const tag of tags) {
      const degree = tagDegrees[tag.id] || 0;
      const size = Math.min(56, Math.max(24, 20 + degree * 3.5));
      const isAuto = tag.source === "automatic";
      const tagColor = isAuto ? "#78909C" : "#3F51B5";

      elements.push({
        group: "nodes",
        data: {
          id: tag.id,
          label: tag.label,
          nodeType: "tag",
          tagId: tag.tagId,
          source: tag.source,
          isBroad: !!tag.isBroad,
          size: size,
          color: tagColor
        }
      });
    }

    // Add edges
    for (let i = 0; i < edges.length; i++) {
      const edge = edges[i];
      elements.push({
        group: "edges",
        data: {
          id: "e_" + edge.source + "_" + edge.target,
          source: edge.source,
          target: edge.target
        }
      });
    }

    cy.batch(() => {
      cy.elements().remove();
      cy.add(elements);

      // Apply broad tags visibility
      applyBroadTagsVisibility(broadTagsVisible);
    });

    const layout = cy.layout({
      name: "cose",
      animate: false,
      randomize: false,
      fit: true,
      padding: 30,
      nodeRepulsion: function () { return 2048; },
      idealEdgeLength: function () { return 50; }
    });

    layout.one("layoutstop", function () {
      const duration = performance.now() - startTime;
      sendToHost("firstFrameRendered", {
        nodeCount: files.length + tags.length,
        edgeCount: edges.length,
        renderDurationMs: Math.round(duration * 100) / 100
      });
    });

    layout.run();
  }

  function applyBroadTagsVisibility(visible) {
    if (!cy) return;
    cy.nodes("[?isBroad]").forEach(node => {
      if (visible) {
        node.style("display", "element");
        node.connectedEdges().style("display", "element");
      } else {
        node.style("display", "none");
        node.connectedEdges().style("display", "none");
      }
    });
  }

  function setBroadTagsVisible(visible) {
    broadTagsVisible = !!visible;
    if (cy) {
      cy.batch(() => {
        applyBroadTagsVisibility(broadTagsVisible);
      });
      cy.fit(null, 30);
    }
  }

  function fitViewport() {
    if (cy) {
      cy.resize();
      cy.fit(null, 30);
    }
  }

  function selectNode(nodeId) {
    if (!cy) return;
    cy.batch(() => {
      cy.nodes().unselect().removeClass("selected");
      if (nodeId) {
        const target = cy.$id(nodeId);
        if (target && target.length > 0) {
          target.select().addClass("selected");
        }
      }
    });
  }

  function handleMessage(message) {
    if (!message || !message.type) return;

    try {
      switch (message.type) {
        case "renderSnapshot":
          renderSnapshot(message.payload);
          break;
        case "fitViewport":
          fitViewport();
          break;
        case "selectNode":
          selectNode(message.payload && message.payload.nodeId);
          break;
        case "setBroadTagsVisible":
          setBroadTagsVisible(message.payload && message.payload.visible);
          break;
        default:
          console.warn("Unknown message type:", message.type);
      }
    } catch (err) {
      console.error("Error handling message:", err);
      sendToHost("error", { message: err.message || String(err) });
    }
  }

  // Initialize
  try {
    initCytoscape();

    window.addEventListener("resize", function () {
      if (cy) {
        cy.resize();
      }
    });

    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener("message", function (event) {
        handleMessage(event.data);
      });
    }

    sendToHost("ready");
  } catch (err) {
    console.error("Initialization error:", err);
    sendToHost("error", { message: err.message || String(err) });
  }
})();
