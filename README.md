(Forked from https://github.com/mmozeiko/c_d2d_dwrite)

# Direct2D and DirectWrite Odin bindings

Bindings are generated from [Win32 Metadata][] .winmd file (included in repo).

Overloaded methods have incremental numbers appended to disambiguate them (can't use odin procedure groups
since these are function pointers, not regular procedures).

For example, interface [ID2D1DeviceContext][] has [CreateBitmap][ID2D1DeviceContext-CreateBitmap] method, but
its parent interface [ID2D1RenderTarget][] also has [CreateBitmap][ID2D1RenderTarget-CreateBitmap] method - with
different arguments. So whichever method comes last is renamed to `CreateBitmap1`. In this case it will be
`ID2D1DeviceContext->CreateBitmap1`.

Some methods have more overloads, so to use them you will need to append suffix with 2, 3, 4, 5 or larger number.

Some functions in D2D / DWrite returning structs violate COM ABI.
Read [Direct 2D Scene of the Accident][d2d-accident] for more information.
For these functions, the signature is rewritten so the functions have no return value, and take a pointer to the result as their last parameter.

For example, `ID2D1Bitmap::GetSize` looks like this in C++ and Odin respectively:
```
D2D1_SIZE_F GetSize()
GetSize: proc "system" (this: ^ID2D1Bitmap, _return: ^D2D_SIZE_F)
```

# Running generator

Run `dotnet run --project Generator`. It will write output `dwrite.odin`, which contains bindings for both DirectWrite and Direct2D.

[Win32 Metadata]: https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/
[ID2D1DeviceContext]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1devicecontext
[ID2D1RenderTarget]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1rendertarget
[ID2D1DeviceContext-CreateBitmap]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-createbitmap(d2d1_size_u_constd2d1_bitmap_properties__id2d1bitmap)
[ID2D1RenderTarget-CreateBitmap]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-createbitmap(d2d1_size_u_constvoid_uint32_constd2d1_bitmap_properties__id2d1bitmap)
[d2d-accident]: https://blog.airesoft.co.uk/2014/12/direct2d-scene-of-the-accident/
