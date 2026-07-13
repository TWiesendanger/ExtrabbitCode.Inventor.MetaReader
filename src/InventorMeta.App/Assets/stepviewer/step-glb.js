(function (root, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
  root.StepGlb = api;
})(typeof self !== "undefined" ? self : this, function () {
  const ARRAY_BUFFER = 34962;
  const ELEMENT_ARRAY_BUFFER = 34963;
  const FLOAT = 5126;
  const UNSIGNED_SHORT = 5123;
  const UNSIGNED_INT = 5125;
  const TRIANGLES = 4;

  function flatten(values) {
    if (!values || values.length === 0) {
      return [];
    }
    if (Array.isArray(values[0])) {
      const out = [];
      for (const row of values) {
        for (const value of row) {
          out.push(value);
        }
      }
      return out;
    }
    return values;
  }

  function align4(value) {
    return (value + 3) & ~3;
  }

  function normalizedColor(color) {
    if (!color || color.length < 3) {
      return [0.72, 0.74, 0.76, 1.0];
    }
    const rgb = [color[0], color[1], color[2]].map((c) => c > 1 ? c / 255 : c);
    return [rgb[0], rgb[1], rgb[2], 1.0];
  }

  function colorKey(color) {
    return normalizedColor(color).map((c) => c.toFixed(4)).join(",");
  }

  function vec3MinMax(values) {
    const min = [Number.POSITIVE_INFINITY, Number.POSITIVE_INFINITY, Number.POSITIVE_INFINITY];
    const max = [Number.NEGATIVE_INFINITY, Number.NEGATIVE_INFINITY, Number.NEGATIVE_INFINITY];
    for (let i = 0; i + 2 < values.length; i += 3) {
      min[0] = Math.min(min[0], values[i]);
      min[1] = Math.min(min[1], values[i + 1]);
      min[2] = Math.min(min[2], values[i + 2]);
      max[0] = Math.max(max[0], values[i]);
      max[1] = Math.max(max[1], values[i + 1]);
      max[2] = Math.max(max[2], values[i + 2]);
    }
    return { min, max };
  }

  function buildGlbFromOcctResult(result, options) {
    if (!result || result.success === false || !Array.isArray(result.meshes)) {
      throw new Error(result && result.error ? result.error : "OCCT did not return mesh data.");
    }

    const gltf = {
      asset: {
        version: "2.0",
        generator: "ExtrabbitCode.Inventor.MetaReader + occt-import-js"
      },
      scene: 0,
      scenes: [{ nodes: [0] }],
      nodes: [{ name: (options && options.name) || (result.root && result.root.name) || "STEP model", children: [] }],
      meshes: [],
      materials: [],
      buffers: [],
      bufferViews: [],
      accessors: []
    };

    const materialByColor = new Map();
    const binaryParts = [];
    let binaryLength = 0;

    function addBufferView(typedArray, target) {
      const bytes = new Uint8Array(typedArray.buffer, typedArray.byteOffset, typedArray.byteLength);
      const byteOffset = binaryLength;
      binaryParts.push(bytes);
      binaryLength += bytes.byteLength;
      const padded = align4(binaryLength);
      if (padded > binaryLength) {
        binaryParts.push(new Uint8Array(padded - binaryLength));
        binaryLength = padded;
      }
      gltf.bufferViews.push({ buffer: 0, byteOffset, byteLength: bytes.byteLength, target });
      return gltf.bufferViews.length - 1;
    }

    function addAccessor(bufferView, componentType, type, count, minMax) {
      const accessor = { bufferView, componentType, count, type };
      if (minMax) {
        accessor.min = minMax.min;
        accessor.max = minMax.max;
      }
      gltf.accessors.push(accessor);
      return gltf.accessors.length - 1;
    }

    function materialIndex(color) {
      const key = colorKey(color);
      if (materialByColor.has(key)) {
        return materialByColor.get(key);
      }
      const baseColorFactor = normalizedColor(color);
      gltf.materials.push({
        name: "mat_" + gltf.materials.length,
        pbrMetallicRoughness: {
          baseColorFactor,
          metallicFactor: 0.0,
          roughnessFactor: 0.62
        },
        doubleSided: true
      });
      const index = gltf.materials.length - 1;
      materialByColor.set(key, index);
      return index;
    }

    let meshNumber = 0;
    for (const sourceMesh of result.meshes) {
      const positions = flatten(sourceMesh.attributes && sourceMesh.attributes.position && sourceMesh.attributes.position.array);
      const indices = flatten(sourceMesh.index && sourceMesh.index.array);
      if (!positions || positions.length < 9 || !indices || indices.length < 3) {
        continue;
      }

      const positionArray = Float32Array.from(positions);
      const positionView = addBufferView(positionArray, ARRAY_BUFFER);
      const positionAccessor = addAccessor(positionView, FLOAT, "VEC3", positionArray.length / 3, vec3MinMax(positionArray));

      let normalAccessor = null;
      const normals = flatten(sourceMesh.attributes && sourceMesh.attributes.normal && sourceMesh.attributes.normal.array);
      if (normals && normals.length === positions.length) {
        const normalArray = Float32Array.from(normals);
        const normalView = addBufferView(normalArray, ARRAY_BUFFER);
        normalAccessor = addAccessor(normalView, FLOAT, "VEC3", normalArray.length / 3, null);
      }

      let maxIndex = 0;
      for (const index of indices) {
        maxIndex = Math.max(maxIndex, index);
      }
      const indexArray = maxIndex <= 65535 ? Uint16Array.from(indices) : Uint32Array.from(indices);
      const indexComponentType = maxIndex <= 65535 ? UNSIGNED_SHORT : UNSIGNED_INT;
      const indexView = addBufferView(indexArray, ELEMENT_ARRAY_BUFFER);
      const indexAccessor = addAccessor(indexView, indexComponentType, "SCALAR", indexArray.length, {
        min: [0],
        max: [maxIndex]
      });

      const primitive = {
        attributes: { POSITION: positionAccessor },
        indices: indexAccessor,
        material: materialIndex(sourceMesh.color),
        mode: TRIANGLES
      };
      if (normalAccessor !== null) {
        primitive.attributes.NORMAL = normalAccessor;
      }

      gltf.meshes.push({
        name: sourceMesh.name || "Body " + (meshNumber + 1),
        primitives: [primitive]
      });
      const meshIndex = gltf.meshes.length - 1;
      gltf.nodes.push({ name: sourceMesh.name || "Body " + (meshNumber + 1), mesh: meshIndex });
      gltf.nodes[0].children.push(gltf.nodes.length - 1);
      meshNumber++;
    }

    if (gltf.meshes.length === 0) {
      throw new Error("OCCT imported the STEP file, but produced no triangulated meshes.");
    }

    const binary = new Uint8Array(binaryLength);
    let cursor = 0;
    for (const part of binaryParts) {
      binary.set(part, cursor);
      cursor += part.byteLength;
    }
    gltf.buffers.push({ byteLength: binary.byteLength });

    const encoder = new TextEncoder();
    const jsonBytes = encoder.encode(JSON.stringify(gltf));
    const jsonLength = align4(jsonBytes.length);
    const totalLength = 12 + 8 + jsonLength + 8 + binary.byteLength;
    const glb = new Uint8Array(totalLength);
    const view = new DataView(glb.buffer);
    view.setUint32(0, 0x46546c67, true);
    view.setUint32(4, 2, true);
    view.setUint32(8, totalLength, true);
    view.setUint32(12, jsonLength, true);
    view.setUint32(16, 0x4e4f534a, true);
    glb.set(jsonBytes, 20);
    glb.fill(0x20, 20 + jsonBytes.length, 20 + jsonLength);
    const binHeader = 20 + jsonLength;
    view.setUint32(binHeader, binary.byteLength, true);
    view.setUint32(binHeader + 4, 0x004e4942, true);
    glb.set(binary, binHeader + 8);
    return glb;
  }

  function estimateOcctStats(result) {
    let vertices = 0;
    let triangles = 0;
    if (result && Array.isArray(result.meshes)) {
      for (const mesh of result.meshes) {
        const positions = flatten(mesh.attributes && mesh.attributes.position && mesh.attributes.position.array);
        const indices = flatten(mesh.index && mesh.index.array);
        vertices += positions ? positions.length / 3 : 0;
        triangles += indices ? indices.length / 3 : 0;
      }
    }
    return { meshes: result && Array.isArray(result.meshes) ? result.meshes.length : 0, vertices, triangles };
  }

  return { buildGlbFromOcctResult, estimateOcctStats };
});
