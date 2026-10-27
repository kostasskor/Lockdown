"""
Generates a seamless 1K kalderimi (traditional Greek stone lane) PBR texture set.
Adheres strictly to the visual reference requirements:
1. Subangular polygonal fieldstones in a tight crazy-paving mosaic (medium/large 20-35cm + filler stones 5-12cm).
2. Wide, pronounced pale off-white / light dusty beige mortar joints (2-5cm).
3. Strong per-stone tonal contrast: warm limestone greys, bleached buff/tan, ochre accents, neutral dark grey (no blue tint).
4. Seamless periodic tiling in UV space.
Outputs:
- Assets/Lockdown/Textures/Ground/T_Kalderimi_diff.png
- Assets/Lockdown/Textures/Ground/T_Kalderimi_nor.png
- Assets/Lockdown/Textures/Ground/T_Kalderimi_MAOS.png
"""
import os
import numpy as np
from PIL import Image
import scipy.ndimage as ndi
from scipy.spatial import cKDTree

def generate_kalderimi(out_dir, size=1024, seed=124):
    os.makedirs(out_dir, exist_ok=True)
    rng = np.random.default_rng(seed)
    N = size

    # 1. Primary seeds (medium/large stones ~25-35cm on a 2.4m patch -> ~46 stones)
    n_primary = 46
    px = rng.uniform(0.0, 1.0, n_primary)
    py = rng.uniform(0.0, 1.0, n_primary)

    # Lloyd relaxation with periodic toroidal boundary
    for _ in range(5):
        dx = px[:, None] - px[None, :]
        dy = py[:, None] - py[None, :]
        dx = (dx + 0.5) % 1.0 - 0.5
        dy = (dy + 0.5) % 1.0 - 0.5
        dist = np.sqrt(dx**2 + dy**2) + 1e-6
        force = np.where((dist < 0.16) & (dist > 1e-4), (0.16 - dist) / dist, 0.0)
        px = (px + np.sum(dx * force, axis=1) * 0.025) % 1.0
        py = (py + np.sum(dy * force, axis=1) * 0.025) % 1.0

    # 2. Filler stone seeds (~6-12cm) inserted in triangular corner voids
    n_filler = 38
    fx, fy = [], []
    for _ in range(400):
        rx, ry = rng.uniform(0.0, 1.0), rng.uniform(0.0, 1.0)
        dx = (px - rx + 0.5) % 1.0 - 0.5
        dy = (py - ry + 0.5) % 1.0 - 0.5
        d = np.min(np.sqrt(dx**2 + dy**2))
        if 0.065 < d < 0.11 and len(fx) < n_filler:
            fx.append(rx)
            fy.append(ry)

    all_x = np.concatenate([px, np.array(fx)])
    all_y = np.concatenate([py, np.array(fy)])
    n_total = len(all_x)

    # 3. 3x3 periodic tiling for Voronoi cKDTree
    reps_x, reps_y, reps_id = [], [], []
    for ox in [-1, 0, 1]:
        for oy in [-1, 0, 1]:
            reps_x.append(all_x + ox)
            reps_y.append(all_y + oy)
            reps_id.append(np.arange(n_total))
    reps_x = np.concatenate(reps_x)
    reps_y = np.concatenate(reps_y)
    reps_id = np.concatenate(reps_id)

    seed_coords = np.stack([reps_x, reps_y], axis=1)
    tree = cKDTree(seed_coords)

    # 4. Periodic coordinate grid + organic domain warping (subangular edges)
    grid_y, grid_x = np.mgrid[0:N, 0:N] / float(N)

    def periodic_noise(sigma, scale):
        w = rng.normal(0.0, 1.0, (N, N))
        pad = int(sigma * 3)
        padded = np.pad(w, pad, mode='wrap')
        filtered = ndi.gaussian_filter(padded, sigma=sigma)[pad:-pad, pad:-pad]
        return filtered * scale

    warp_x = periodic_noise(8.0, 0.007) + periodic_noise(2.5, 0.002)
    warp_y = periodic_noise(8.0, 0.007) + periodic_noise(2.5, 0.002)

    qx = (grid_x + warp_x) % 1.0
    qy = (grid_y + warp_y) % 1.0

    eval_coords = np.stack([qx.ravel(), qy.ravel()], axis=1)
    k_neighbors = 8
    dists, indices = tree.query(eval_coords, k=k_neighbors)

    cell_id = reps_id[indices[:, 0]].reshape(N, N)

    p1 = seed_coords[indices[:, 0]]
    d1_sq = dists[:, 0] ** 2
    pj = seed_coords[indices[:, 1:]]
    dp = pj - p1[:, None, :]
    L1j = np.linalg.norm(dp, axis=2) + 1e-6
    dj_sq = dists[:, 1:] ** 2
    boundary_dists = (dj_sq - d1_sq[:, None]) / (2.0 * L1j)

    min_dist = np.min(boundary_dists, axis=1).reshape(N, N)
    min_dist = np.maximum(min_dist, 0.0)

    # 5. Mortar joint mask (approx 2.5 - 4 cm wide in a 2.4m patch -> ~0.013 in UV)
    mortar_half = 0.013
    mortar_mask = np.clip(1.0 - min_dist / mortar_half, 0.0, 1.0)
    mortar_mask = mortar_mask * mortar_mask * (3.0 - 2.0 * mortar_mask)

    # 6. Stone palettes with strong adjacent contrast (Warm Cretan limestone tones, neutral dark grey)
    palettes = [
        np.array([150.0, 146.0, 140.0], dtype=np.float32),  # 0: Warm limestone grey
        np.array([182.0, 174.0, 156.0], dtype=np.float32),  # 1: Bleached buff/tan
        np.array([168.0, 146.0, 120.0], dtype=np.float32),  # 2: Muted ochre accent
        np.array([130.0, 128.0, 125.0], dtype=np.float32),  # 3: Neutral dark limestone grey (no blue)
        np.array([166.0, 162.0, 154.0], dtype=np.float32),  # 4: Weathered pale stone
        np.array([142.0, 136.0, 128.0], dtype=np.float32),  # 5: Muted umber limestone
    ]

    cell_colors = np.zeros((n_total, 3), dtype=np.float32)
    c_indices = rng.permutation(n_total)
    for i in c_indices:
        choice = rng.integers(0, len(palettes))
        col = palettes[choice].copy()
        col *= float(rng.uniform(0.93, 1.07))
        col += rng.uniform(-2.5, 2.5, size=3)
        cell_colors[i] = col

    stone_albedo = cell_colors[cell_id]

    grain = periodic_noise(1.8, 5.0) + periodic_noise(0.9, 3.0)
    stone_albedo += grain[:, :, None]

    stone_height = np.clip(min_dist / 0.042, 0.0, 1.0)
    stone_profile = np.sin(stone_height * (np.pi * 0.5))
    stone_albedo *= (0.86 + 0.14 * stone_profile)[:, :, None]

    # 7. Mortar: pale off-white / light dusty beige with porous sand grain
    mortar_base = np.array([218.0, 214.0, 204.0], dtype=np.float32)
    mortar_sand = periodic_noise(0.8, 7.0)
    mortar_albedo = mortar_base + mortar_sand[:, :, None]
    crevice_shadow = np.clip(min_dist / 0.008, 0.0, 1.0)
    mortar_albedo *= (0.89 + 0.11 * crevice_shadow)[:, :, None]

    # Final Albedo blend
    diffuse = stone_albedo * (1.0 - mortar_mask[:, :, None]) + mortar_albedo * (mortar_mask[:, :, None])
    diffuse = np.clip(diffuse, 0.0, 255.0).astype(np.uint8)

    # 8. Normal Map generation (softened strength so path reads flat and walkable)
    height = stone_profile * (1.0 - mortar_mask) * 0.75
    height += (1.0 - mortar_mask) * periodic_noise(2.2, 0.03)

    pad = 2
    h_pad = np.pad(height, pad, mode='wrap')
    sobel_x = np.array([[-1, 0, 1], [-2, 0, 2], [-1, 0, 1]], dtype=np.float32) / 8.0
    sobel_y = np.array([[-1, -2, -1], [0, 0, 0], [1, 2, 1]], dtype=np.float32) / 8.0

    gx = ndi.convolve(h_pad, sobel_x)[pad:-pad, pad:-pad]
    gy = ndi.convolve(h_pad, sobel_y)[pad:-pad, pad:-pad]

    strength = 3.5
    nz = np.ones_like(gx)
    nx = -gx * strength
    ny = -gy * strength
    length = np.sqrt(nx**2 + ny**2 + nz**2)
    nx /= length
    ny /= length
    nz /= length

    nor_r = ((nx * 0.5 + 0.5) * 255.0).clip(0, 255).astype(np.uint8)
    nor_g = ((ny * 0.5 + 0.5) * 255.0).clip(0, 255).astype(np.uint8)
    nor_b = ((nz * 0.5 + 0.5) * 255.0).clip(0, 255).astype(np.uint8)
    normal = np.stack([nor_r, nor_g, nor_b], axis=2)

    # 9. MAOS map (Metallic, AO, Detail, Smoothness)
    ao = np.clip(0.70 + 0.30 * (min_dist / 0.02), 0.55, 1.0) * (0.88 + 0.12 * stone_profile)
    ao_val = (ao * 255.0).clip(0, 255).astype(np.uint8)

    metallic_val = np.zeros((N, N), dtype=np.uint8)
    detail_val = np.full((N, N), 255, dtype=np.uint8)

    # Subtle matte stone finish, fully rough mortar
    smoothness = 0.12 * stone_profile * (1.0 - mortar_mask) + 0.04 * mortar_mask
    smooth_val = (smoothness * 255.0).clip(0, 255).astype(np.uint8)

    maos = np.stack([metallic_val, ao_val, detail_val, smooth_val], axis=2)

    # Save outputs
    diff_path = os.path.join(out_dir, "T_Kalderimi_diff.png")
    nor_path = os.path.join(out_dir, "T_Kalderimi_nor.png")
    maos_path = os.path.join(out_dir, "T_Kalderimi_MAOS.png")

    Image.fromarray(diffuse).save(diff_path, "PNG", optimize=True)
    Image.fromarray(normal).save(nor_path, "PNG", optimize=True)
    Image.fromarray(maos, "RGBA").save(maos_path, "PNG", optimize=True)

    print(f"Generated refined Kalderimi textures in {out_dir}")

if __name__ == "__main__":
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else r"Assets\Lockdown\Textures\Ground"
    generate_kalderimi(out)

