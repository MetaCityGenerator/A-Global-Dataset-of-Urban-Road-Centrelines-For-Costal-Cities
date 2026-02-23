# 🌍 A Global Dataset of Urban Road Centrelines for 2000+ Coastal Cities

[![License: CC BY 4.0](https://img.shields.io/badge/License-CC%20BY%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by/4.0/)
[![Dataset](https://img.shields.io/badge/Dataset-Google%20Drive-4285F4?logo=googledrive&logoColor=white)](https://drive.google.com/drive/folders/1fjjiuFC3kgiojk5mqKITHmYBqh3BZQ1t?usp=sharing)
[![Cities](https://img.shields.io/badge/Cities-2%2C588-brightgreen)](.)
[![Countries](https://img.shields.io/badge/Countries-110-blue)](.)

> **Unified road centrelines for spatial analysis** — Transforming traffic-oriented road networks into spatially-coherent centreline representations using Voronoi tessellation.

![Methodology Overview](001.png)

---

## 📖 Overview

Coastal cities, home to **over 40% of the global population**, require accurate spatial data infrastructure for urban planning and climate adaptation. However, existing road network datasets like OpenStreetMap represent roads in **traffic-oriented format**, storing multi-lane roads as multiple parallel segments.

**The Problem:** When a four-lane road is represented as four separate lines, spatial analyses such as space syntax and centrality calculations incorrectly count it four times, significantly **distorting measurements of urban spatial structure**.

**Our Solution:** We present a comprehensive dataset of **unified road centrelines for 2,588 coastal cities across 110 countries**, automatically extracted using Voronoi tessellation combined with adaptive spatial partitioning.

---

## 🌐 Geographic Coverage

| Continent | Number of Cities |
|-----------|------------------|
| 🌏 Asia | 1,189 |
| 🌍 Europe | 357 |
| 🌎 North America | 364 |
| 🌍 Africa | 312 |
| 🌎 South America | 256 |
| 🌏 Oceania | 110 |
| **Total** | **2,588** |

---

## ⚡ Key Features

- **🚀 High Performance**: Processes complex urban networks (7,000+ segments) in ~2 minutes vs 4.5 hours for conventional approaches
- **✅ Validated Quality**: Strong correlation (R² > 0.85) with manually-drawn axial maps
- **📊 GeoParquet Format**: Efficient columnar storage (EPSG:4326), ~9.6M km total road length, ~42M road segments globally
- **🔗 Full Traceability**: Original OSM IDs preserved for source verification
- **🌍 Global Coverage**: 110 countries across all inhabited continents

---

## 📁 Dataset Structure

The dataset uses a **flat layout** with GeoParquet files:

```
centroidline_update/data/output/
├── raw_road_networks/              # Original OSM multi-lane roads
│   ├── {Country}_{City}_roads.parquet
│   └── ...
└── centerline_road_networks/       # Extracted centerlines
    ├── {Country}_{City}_centerline.parquet
    └── ...
```

Example file names: `China_Shanghai_centerline.parquet`, `Indonesia_Jakarta_centerline.parquet`

---

## 📋 Data Attributes

### Road Segment Properties (GeoParquet)

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

### Global Statistics (this release)

| Metric | Value |
|--------|-------|
| Total cities | 2,588 |
| Total road length | ~9,639,041 km |
| Total road segments | ~42,097,693 |
| Avg. length preservation rate | 73.53% |
| Avg. road length per city | ~3,725 km |

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

# Load road centrelines for a specific city (GeoParquet format)
roads = gpd.read_parquet("centerline_road_networks/China_Shanghai_centerline.parquet")

# Basic statistics
print(f"Total segments: {len(roads)}")
print(f"Total length: {roads.geometry.length.sum()/1000:.2f} km")

# Visualize
roads.plot(figsize=(12, 12), linewidth=0.5)
```

### QGIS

1. Open QGIS
2. Drag and drop the `.parquet` file into the map canvas (or use Layer → Add Layer → Add Vector Layer)
3. Style using `road_type` for categorical visualization

### Network Analysis

Convert GeoParquet to a graph for network analysis (e.g., using `geopandas` + `networkx` or `momepy`):

```python
import geopandas as gpd
import momepy

roads = gpd.read_parquet("centerline_road_networks/China_Shanghai_centerline.parquet")
G = momepy.gdf_to_nx(roads)
# Calculate centrality measures, etc.
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
