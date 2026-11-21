#include <manifold/manifold.h>
#include <algorithm>
#include <cmath>
#include <memory>
#include <vector>

using manifold::Manifold;
using manifold::MeshGL64;

struct Result {
  std::vector<std::vector<MeshGL64>> pieces;
};

// All coordinates are print-scale millimetres. Index buffers are local to each
// input mesh. Results stay owned here until the managed caller releases them.
extern "C" void* td_clean(int count, const int* nv, const int* nf,
                         const double* vertices, const int* faces, int* failed) noexcept {
  try {
    auto output = std::make_unique<Result>();
    std::vector<Manifold> cutters;
    const auto padding = Manifold::Cube({0.1,0.1,0.1},true);
    for (int i=0;i<count;++i) {
      *failed = i;
      MeshGL64 mesh;
      mesh.vertProperties.assign(vertices,vertices+3*nv[i]);
      mesh.triVerts.assign(faces,faces+3*nf[i]);
      vertices += 3*nv[i]; faces += 3*nf[i];
      mesh.Merge();
      Manifold source(mesh);
      if (source.Status()!=Manifold::Error::NoError || source.Volume()<=0) return nullptr;
      auto remaining = source;
      auto bounds = source.BoundingBox();
      for (const auto& cutter : cutters)
        if (bounds.DoesOverlap(cutter.BoundingBox())) remaining = remaining - cutter;
      if (remaining.Status()!=Manifold::Error::NoError) return nullptr;
      auto& parts = output->pieces.emplace_back();
      for (auto& part : remaining.Decompose())
        if (part.Volume() >= .001) parts.push_back(part.Simplify(.002).GetMeshGL64());
      auto expanded = source.MinkowskiSum(padding);
      if (expanded.Status()!=Manifold::Error::NoError) return nullptr;
      cutters.emplace_back(std::move(expanded));
    }
    return output.release();
  } catch (...) { return nullptr; }
}
extern "C" int td_count(Result* r,int i) { return static_cast<int>(r->pieces[i].size()); }
extern "C" void td_mesh(Result* r,int i,int j,int* nv,int* nf,const double** vertices,const uint64_t** faces) {
  const auto& mesh = r->pieces[i][j];
  *nv=static_cast<int>(mesh.NumVert()); *nf=static_cast<int>(mesh.NumTri());
  *vertices=mesh.vertProperties.data(); *faces=mesh.triVerts.data();
}
extern "C" void td_release(Result* r) { delete r; }
