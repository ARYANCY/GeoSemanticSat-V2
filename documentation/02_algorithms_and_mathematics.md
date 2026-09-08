# 02. Mathematical & Algorithmic Specifications

> For formulas and thresholds verified against the current source, use
> [07_implementation_reference.md](07_implementation_reference.md). This page
> contains historical design material and may describe planned components.

**Module:** UpaGraha Advanced Mathematical Foundations & Core Remote Sensing Algorithms  
**Status:** Validated & Implemented (September 2026)

---

## 1. Algorithmic Overview & Performance Complexity

| Algorithm | Traditional / Naive Approach | UpaGraha Optimized Implementation | Asymptotic Complexity | Key Advantage |
| :--- | :--- | :--- | :---: | :--- |
| **Change Detection** | Single-band subtraction $|I_2 - I_1|$ | Multi-Band Change Vector Analysis (CVA) + Trajectory Direction | $O(B \cdot H \cdot W)$ | Discerns construction from vegetation loss and water dynamics |
| **Onset Estimation** | Single-baseline comparison ($T_i - T_0$) | Sequential CUSUM with Rolling Variance ($3.5\sigma$) | $O(T)$ | Immune to seasonal vegetation drift and phenological false alarms |
| **Jitter Suppression** | Integer shift testing ($\pm 1\text{ px}$) | Sub-Pixel Bilinear & Quadratic Peak Interpolation | $O(K \cdot P^2)$ | Filters fractional $0.1 - 0.8\text{ px}$ orthorectification jitter |
| **Radiometric Fit** | Ordinary Least Squares (OLS) | Iteratively Reweighted Least Squares (IRLS) with Tukey Biweight | $O(N_{\text{PIF}} \cdot I)$ | Outlier-resistant PIF regression under large-scale land changes |
| **Spatial Clustering** | Brute-force all-pairs DBSCAN | Spatial Hash Grid Pre-filtered DBSCAN | $O(N \log N)$ | Sub-second clustering across 100,000 image patches |
| **Vector Similarity** | Full heap-cloning linear scan | In-Place SIMD AVX2 Dot-Product Search (`ReaderWriterLockSlim`) | $O(D \cdot N / 8)$ | Zero-allocation parallel read concurrency ($< 1.0\text{ms}$) |
| **Surface Area Math** | Flat Cartesian planar $W \times H \times \text{GSD}^2$ | WGS84 Ellipsoidal Geodesic Area ($\cos\phi$ corrected) | $O(1)$ | Prevents 15%-40% area distortion at non-equatorial latitudes |

---

## 2. Multi-Band Change Vector Analysis (CVA)

```mermaid
graph LR
    subgraph Multi-Spectral Epochs
        T1[Epoch T1: Multi-Band Bands]
        T2[Epoch T2: Multi-Band Bands]
    end

    subgraph CVA Processing Engine
        Diff[Compute Spectral Delta Matrix]
        Mag[Spectral Magnitude: Euclidean Norm]
        Traj[Spectral Angle: Trajectory Direction]
    end

    subgraph Classification Decisions
        C1[NDVI Loss + NDBI Gain -> Construction]
        C2[NDVI Loss + BSI Gain -> Clearance]
        C3[NDWI Delta -> Water Variation]
        C4[Low Magnitude -> No Change]
    end

    T1 --> Diff
    T2 --> Diff
    Diff --> Mag
    Diff --> Traj
    Mag --> C4
    Traj --> C1
    Traj --> C2
    Traj --> C3
```

### Mathematical Formulation:
For an observation with $B$ normalized physical spectral bands:
$$\Delta \vec{\rho}(x, y) = \begin{pmatrix} \rho_1^{(T_2)}(x, y) - \rho_1^{(T_1)}(x, y) \\ \vdots \\ \rho_B^{(T_2)}(x, y) - \rho_B^{(T_1)}(x, y) \end{pmatrix}$$
1. **Spectral Magnitude ($M$):**
   $$M(x, y) = \|\Delta \vec{\rho}(x, y)\|_2 = \sqrt{\sum_{b=1}^B \left( \rho_b^{(T_2)}(x, y) - \rho_b^{(T_1)}(x, y) \right)^2}$$
2. **Trajectory Vector ($\vec{\theta}$):**
   $$\vec{\theta}(x, y) = \arctan2\left(\Delta \text{NDBI}(x, y), \Delta \text{NDVI}(x, y)\right)$$

---

## 3. Sequential Cumulative Sum (CUSUM) Onset Estimation

To detect the exact historical pass when ground physical activity commenced without being fooled by seasonal moisture/vegetation cycles:

```mermaid
flowchart TD
    A[Chronological Series T_1 ... T_k] --> B[Generate Cloud & Shadow Quality Masks]
    B --> C[Filter Usable Observations: >= 40% Valid Pixels]
    C --> D[Establish Moving Baseline: Mean & StdDev from initial epochs]
    D --> E[Sequential CUSUM Loop]
    E -->|CUSUM > 3.5 Sigma| F[Flag Earliest Statistically Verified Onset Date]
    E -->|Within Control Limits| G[Proceed to Next Temporal Pass]
```

### Formulation:
Given time-series observations $Y_1, Y_2, \dots, Y_n$ with baseline mean $\mu_0$ and sample variance $\sigma_0^2$:
$$S_k^+ = \max\left(0, S_{k-1}^+ + (Y_k - \mu_0) - K\right), \quad S_0^+ = 0$$
$$S_k^- = \max\left(0, S_{k-1}^- - (Y_k - \mu_0) - K\right), \quad S_0^- = 0$$
where $K = 0.5 \sigma_0$ (slack allowance). An alarm is flagged at step $k^*$ when:
$$\max\left(S_{k^*}^+, S_{k^*}^-\right) > H, \quad H = 3.5 \sigma_0$$

---

## 4. Sub-Pixel Surface Peak Interpolation for Jitter Suppression

Satellite orthorectification errors frequently create fractional misregistrations ($\Delta x, \Delta y \in [-1.0, 1.0]$).

### Parabolic Peak Formulation:
Given a $3 \times 3$ correlation/SSIM grid evaluated at integer shifts around the center $(0, 0)$:
$$\text{Peak Shift } \Delta x_{\text{sub}} = x_{\max} + \frac{C(y_{\max}, x_{\max}-1) - C(y_{\max}, x_{\max}+1)}{2 \left(2 C(y_{\max}, x_{\max}) - C(y_{\max}, x_{\max}-1) - C(y_{\max}, x_{\max}+1)\right)}$$
$$\text{Peak Shift } \Delta y_{\text{sub}} = y_{\max} + \frac{C(y_{\max}-1, x_{\max}) - C(y_{\max}+1, x_{\max})}{2 \left(2 C(y_{\max}, x_{\max}) - C(y_{\max}-1, x_{\max}) - C(y_{\max}+1, x_{\max})\right)}$$
If the optimal sub-pixel alignment reduces spectral residual error by $> 50\%$, the candidate change event is classified as co-registration jitter and suppressed.

---

## 5. Iteratively Reweighted Least Squares (IRLS) with Tukey Biweight

To estimate linear relative radiometric normalization $Y_{\text{ref}} = m \cdot X_{\text{tgt}} + c$ robust against change outliers:

### Weighting Function:
$$w_i(r_i) = \begin{cases} \left(1 - \left(\frac{r_i}{c_T}\right)^2\right)^2 & \text{if } |r_i| \le c_T \\ 0 & \text{if } |r_i| > c_T \end{cases}$$
where $r_i = y_i - (m x_i + c)$, and $c_T = 4.685 \cdot \text{MAD}(r)$.

---

## 6. Ellipsoidal Geodesic Surface Area Calculation

For an area bounded by pixel dimensions $(W, H)$ with nominal Ground Sampling Distance $\text{GSD}$ at center latitude $\phi$:
$$\Delta x_{\text{ground}} = \text{GSD} \cdot \cos(\phi)$$
$$\Delta y_{\text{ground}} = \text{GSD}$$
$$\text{Area}(\text{meters}^2) = W \cdot H \cdot \text{GSD}^2 \cdot \cos(\phi)$$
$$\text{Area}(\text{hectares}) = \frac{\text{Area}(\text{meters}^2)}{10,000}$$

---

## 7. Geospatial Foundation Models Mathematical Formulations

### 7.1 Any-to-Any Multimodal Cosine Similarity (TerraMind-1.0-base)
For normalized text representation $\vec{u}_{\text{text}} \in \mathbb{R}^{D}$ and multi-spectral patch embedding $\vec{v}_{\text{image}} \in \mathbb{R}^{D}$ across shared semantic subspace $\mathcal{S}$:
$$\text{Sim}_{\text{semantic}}(\vec{u}, \vec{v}) = \frac{\sum_{i \in \mathcal{S}} u_i \cdot v_i}{\sqrt{\sum_{i \in \mathcal{S}} u_i^2} \cdot \sqrt{\sum_{i \in \mathcal{S}} v_i^2}}$$

### 7.2 Spatio-Temporal Sequence & Velocity Dynamics (Prithvi-EO-2.0-600M-TL)
For observation sequence $\mathbf{X} = \{X_{T_1}, X_{T_2}, \dots, X_{T_K}\}$ with latent state embeddings $\vec{e}_k = \Phi(X_{T_k})$:
$$\vec{v}_{\text{temporal}} = \frac{1}{K-1} \sum_{k=1}^{K-1} \|\vec{e}_{k+1} - \vec{e}_k\|_1, \quad \vec{a}_{\text{onset}} = \frac{1}{K-2} \sum_{k=1}^{K-2} \|(\vec{e}_{k+2} - \vec{e}_{k+1}) - (\vec{e}_{k+1} - \vec{e}_k)\|_1$$

### 7.3 Multi-Sensor SAR + Optical Composition (GFM Composition Pretraining)
For optical reflectance matrix $\mathbf{O} \in \mathbb{R}^{B_{\text{opt}} \times H \times W}$ and polarimetric radar backscatter $\mathbf{S} \in \mathbb{R}^{2 \times H \times W}$ (VV, VH):
$$\vec{z}_{\text{composed}} = \text{Normalize}\left( \mathbf{W}_{\text{opt}} \text{Pool}(\mathbf{O}) \oplus \mathbf{W}_{\text{sar}} \left[ \mu(\text{VV}), \sigma(\text{VV}), \mu(\text{VH}), \frac{\mu(\text{VH})}{\mu(\text{VV}) + \epsilon} \right] \right)$$

