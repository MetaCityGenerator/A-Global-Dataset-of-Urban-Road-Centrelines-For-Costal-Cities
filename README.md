# 🌍 A Global Dataset of Urban Road Centrelines for 2000+ Coastal Cities

[![License: CC BY 4.0](https://img.shields.io/badge/License-CC%20BY%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by/4.0/)
[![Dataset](https://img.shields.io/badge/Dataset-Google%20Drive-4285F4?logo=googledrive&logoColor=white)](https://drive.google.com/drive/folders/1fjjiuFC3kgiojk5mqKITHmYBqh3BZQ1t?usp=sharing)
[![Cities](https://img.shields.io/badge/Cities-1%2C437-brightgreen)](.)
[![Countries](https://img.shields.io/badge/Countries-124-blue)](.)

> **Unified road centrelines for spatial analysis** — Transforming traffic-oriented road networks into spatially-coherent centreline representations using Voronoi tessellation.

![Methodology Overview](001.png)

---

## 📖 Overview

Coastal cities, home to **over 40% of the global population**, require accurate spatial data infrastructure for urban planning and climate adaptation. However, existing road network datasets like OpenStreetMap represent roads in **traffic-oriented format**, storing multi-lane roads as multiple parallel segments.

**The Problem:** When a four-lane road is represented as four separate lines, spatial analyses such as space syntax and centrality calculations incorrectly count it four times, significantly **distorting measurements of urban spatial structure**.

**Our Solution:** We present a comprehensive dataset of **unified road centrelines for 2,588 coastal cities across 124 countries**, automatically extracted using Voronoi tessellation combined with adaptive spatial partitioning.

---

## 🌐 Geographic Coverage

| Continent | Number of Cities |
|-----------|------------------|
| 🌏 Asia | 578 |
| 🌍 Europe | 312 |
| 🌎 North America | 234 |
| 🌍 Africa | 156 |
| 🌎 South America | 108 |
| 🌏 Oceania | 49 |
| **Total** | **1,437** |

---

## ⚡ Key Features

- **🚀 High Performance**: Processes complex urban networks (7,000+ segments) in ~2 minutes vs 4.5 hours for conventional approaches
- **✅ Validated Quality**: Strong correlation (R² > 0.85) with manually-drawn axial maps
- **📊 Multiple Formats**: GeoJSON, GeoParquet, GraphML, and ESRI Shapefile
- **🔗 Full Traceability**: Original OSM IDs preserved for source verification
- **🌍 Global Coverage**: 124 countries across all inhabited continents

---

## 📁 Dataset Structure

```
├── Asia/
│   ├── China/
│   │   ├── CHN_Shanghai_RoadCenterlines.geojson
│   │   ├── CHN_Shanghai_RoadCenterlines.graphml
│   │   └── CHN_Shanghai_metadata.json
│   └── ...
├── Europe/
│   └── ...
├── North_America/
│   └── ...
├── Africa/
│   └── ...
├── South_America/
│   └── ...
├── Oceania/
│   └── ...
└── global_statistics.csv
```

---

## 📋 Data Attributes

### Road Segment Properties

| Attribute | Description |
|-----------|-------------|
| `segment_id` | Unique identifier for each centreline segment |
| `original_osm_id` | OpenStreetMap way ID for traceability |
| `road_name` | Local road name (UTF-8 encoded) |
| `road_type` | OSM highway classification (motorway, primary, secondary, etc.) |
| `length_meters` | Geometric length of the centreline segment |
| `width_estimated` | Estimated road width from buffer analysis |
| `extraction_date` | Processing date |
| `source_version` | OSM data timestamp |

### Network Topology (GraphML)

| Node Attributes | Description |
|-----------------|-------------|
| `node_id` | Unique identifier |
| `longitude`, `latitude` | Geographic coordinates |
| `degree` | Number of connected road segments |
| `node_type` | intersection / endpoint / pseudo-node |

---

## 🔧 Methodology

Our approach employs **Voronoi tessellation** to identify the geometric center of road corridors:

1. **Buffer Generation**: 15-meter buffer around original dual-line road network
2. **Artifact Removal**: Eliminate polygons < 200 m² or width < 25 m
3. **Seed Point Generation**: Points at 15-meter intervals along buffered boundaries
4. **Voronoi Tessellation**: Compute using Qhull algorithm
5. **Spatial Partitioning**: 500m × 500m grid for parallel processing
6. **Geometric Simplification**: Douglas-Peucker algorithm (2m tolerance)
7. **Intersection Consolidation**: Merge complex multi-node intersections

---

## 📥 Download

### 🔗 [Download Dataset from Google Drive](https://drive.google.com/drive/folders/1fjjiuFC3kgiojk5mqKITHmYBqh3BZQ1t?usp=sharing)

---

## 💻 Quick Start

### Python (GeoPandas)

```python
import geopandas as gpd

# Load road centrelines for a specific city
roads = gpd.read_file("Asia/China/CHN_Shanghai_RoadCenterlines.geojson")

# Basic statistics
print(f"Total segments: {len(roads)}")
print(f"Total length: {roads['length_meters'].sum()/1000:.2f} km")

# Visualize
roads.plot(figsize=(12, 12), linewidth=0.5)
```

### QGIS

1. Open QGIS
2. Drag and drop the `.geojson` file into the map canvas
3. Style using `road_type` for categorical visualization

### Network Analysis (NetworkX)

```python
import networkx as nx

# Load network topology
G = nx.read_graphml("Asia/China/CHN_Shanghai_RoadCenterlines.graphml")

# Calculate centrality measures
betweenness = nx.betweenness_centrality(G)
closeness = nx.closeness_centrality(G)
```

---

## 📚 Applications

- **🏙️ Space Syntax Analysis**: Unified centrelines enable configuration analysis across large city samples
- **🌊 Climate Adaptation**: Consistent framework for sea-level rise vulnerability analysis
- **🤖 GeoAI/GNN**: Topologically consistent networks for graph neural network training
- **📊 Urban Morphology**: Comparative studies of coastal urban spatial patterns
- **🚗 Transportation Planning**: Network analysis without lane duplication artifacts

---

## 📄 License

This dataset is licensed under the [Creative Commons Attribution 4.0 International License (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/).

You are free to:
- **Share** — copy and redistribute the material in any medium or format
- **Adapt** — remix, transform, and build upon the material for any purpose

Under the following terms:
- **Attribution** — You must give appropriate credit and indicate if changes were made

---

## 🤝 Contributing

We welcome contributions! If you find issues with specific city data or have suggestions for improvements:

1. Open an issue describing the problem
2. For data corrections, specify the city and nature of the issue
3. For methodology improvements, please provide test cases

---

## 📬 Contact

For questions or collaboration inquiries, please:
- Open an issue on this repository
- Contact: xlin0541@outlook.com

---

## 🙏 Acknowledgments

- OpenStreetMap contributors for the source road network data
- Natural Earth for coastline boundary data
- United Nations for population statistics

---

<p align="center">
  <b>Made with ❤️ for the urban research community</b>
</p>
