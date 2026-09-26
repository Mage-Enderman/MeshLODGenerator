# Mesh LOD Generator ⚡

[![Add to VCC](https://img.shields.io/badge/VCC-Add%20to%20VCC-2ea043?style=for-the-badge&logo=vrchat&logoColor=white)](https://mage-enderman.github.io/MeshLODGenerator/?open_vcc=true)
[![Listing](https://img.shields.io/badge/VPM%20Listing-Website-1f6feb?style=for-the-badge)](https://mage-enderman.github.io/MeshLODGenerator/)

A standalone, non-destructive Mesh LOD Generator and Texture Atlas Baker for Unity. Supports **static meshes** and **rigged humanoid/creature characters** (`SkinnedMeshRenderer`) with zero external package dependencies.

Originally built on open-source foundations from [Basis Labs](https://github.com/BasisVR) (`BasisVR/basis`) and extended with bilateral symmetry, per-material boundary seam locking, and standalone binary FBX & GLB exporters.

---

## ✨ Features

- **Universal Support**:
  - **Static GameObjects**: Props, buildings, vehicles, modular environment assets. Combines multiple child meshes into one optimized LOD.
  - **Rigged Characters**: Full humanoid and generic armature support. Preserves bone hierarchies, bindposes, and collapses 2-influence skin weights cleanly.
- **Bilateral Symmetric Decimation**:
  - Automatically identifies left/right mirror vertex pairs across $X=0$.
  - Executes mirror-paired edge collapses to maintain 1:1 identical wireframe topology on both sides of faces and clothing.
  - Centerline vertices are constrained to $X=0$ to prevent zigzags, asymmetry, or seam cracks.
- **Per-Material Separation with Watertight Seam Locking**:
  - Partitions individual submesh materials proportionally while locking shared boundary loops.
  - Re-welds boundaries with zero gaps, tearing, or cracking.
  - Option to **Preserve Original Materials** (multi-submesh LOD) or bake into a unified atlas.
- **Occlusion & Interior Culling**:
  - Raycast hemisphere sampling detects and strips hidden geometry (e.g., body meshes hidden under clothes) before simplification, saving triangles for the visible silhouette.
- **Texture Atlasing & Baking**:
  - Multi-view orthographic capture for Albedo, Emission, and custom shader channels (e.g. `_MetallicGlossMap`).
  - Integrated contact Ambient Occlusion (AO) and chart dilation.
- **Standalone Exporters (Zero Dependencies)**:
  - Export directly to **Binary FBX** (with embedded textures & rig), **GLB (glTF 2.0 Binary)**, **OBJ**, or native Unity **`.asset`**.
- **100% Non-Destructive**:
  - Works entirely in-memory on snapshots. Source scene GameObjects and project assets are never altered.

---

## 💻 Compatibility

- **Unity 2022.3 LTS** (`2022.3.22f1+`)
- **Unity 6** (`6000.0+`)
- Compatible with Built-in Render Pipeline, Universal Render Pipeline (URP), and standard shaders.

---

## 🚀 Installation

### Option A: VRChat Creator Companion (VCC) - One-Click Add
Click the badge below to add the repository directly to your VCC:

[![Add to VCC](https://img.shields.io/badge/VCC-Add%20to%20VCC-2ea043?style=for-the-badge&logo=vrchat&logoColor=white)](https://mage-enderman.github.io/MeshLODGenerator/?open_vcc=true)

**Manual addition in VCC:**
1. In VCC, open **Settings > Packages > Installed Repositories**.
2. Click **Add Repository**.
3. Paste the repository URL:
   ```text
   https://mage-enderman.github.io/MeshLODGenerator/index.json
   ```
4. Click **I Understand, Add Repository**.
5. In your avatar/world project, click **Add** next to **Mesh LOD Generator**.

### Option B: Unity Package Manager (UPM Git URL)
1. In Unity, open **Window > Package Manager**.
2. Click the **`+`** icon in the top-left corner and select **Add package from git URL...**
3. Paste:
   ```text
   https://github.com/Mage-Enderman/MeshLODGenerator.git
   ```

### Option C: `.unitypackage`
Download the latest `.unitypackage` from the [Releases](https://github.com/Mage-Enderman/MeshLODGenerator/releases) tab and drag it into your Unity project.

### Option D: Manual Copy
Download or clone this repository and place the folder into your project's `Assets/` directory.

---

## 🛠️ Quick Start

1. Open the tool from the menu: **Tools > Mesh LOD Generator**.
2. Drag and drop any GameObject from your Scene or Hierarchy into the **Target GameObject** field.
3. Configure your decimation settings:
   - **Target Triangles** or **Decimate by Percentage**.
   - **Symmetric Decimation**: Keep enabled for characters, clothing, and symmetric props.
   - **Separate by Material**: Enable to decimate material groups independently with locked seams.
   - **Cull Occluded Geometry**: Strips geometry occluded under clothes or inside cavities.
4. Click **Generate LOD**.
5. Orbit and zoom in the interactive **3D Preview** viewport.
6. Click **Spawn LOD in Scene**, **Setup LODGroup**, or **Export All (Mesh + Textures)**.

---

## 📄 License & Acknowledgments

This project is licensed under the [MIT License](LICENSE).

- Built on the avatar decimation and baking research from [Basis Labs](https://github.com/BasisVR) (`BasisVR/basis`).
- Standalone extensions, bilateral symmetry engine, and multi-version compatibility maintained by [Mage-Enderman](https://github.com/Mage-Enderman).