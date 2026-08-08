FFmpeg 64-bit static Windows build from www.gyan.dev

Version: 2026-07-27-git-a757b708ae-essentials_build-www.gyan.dev

License: GPL v3

Source Code: https://github.com/FFmpeg/FFmpeg/commit/a757b708ae

git-essentials build configuration:

ARCH                      x86 (generic)
big-endian                no
runtime cpu detection     yes
standalone assembly       yes
x86 assembler             nasm
MMX enabled               yes
MMXEXT enabled            yes
SSE enabled               yes
SSSE3 enabled             yes
AESNI enabled             yes
CLMUL enabled             yes
AVX enabled               yes
AVX2 enabled              yes
AVX-512 enabled           yes
AVX-512ICL enabled        yes
XOP enabled               yes
FMA3 enabled              yes
FMA4 enabled              yes
i686 features enabled     yes
CMOV is fast              yes
EBX available             yes
6 registers available     yes
7 registers available     yes
debug symbols             yes
strip symbols             yes
optimize for size         no
optimizations             yes
static                    yes
shared                    no
network support           yes
threading support         pthreads
safe bitstream reader     yes
texi2html enabled         no
perl enabled              yes
pod2man enabled           yes
makeinfo enabled          yes
makeinfo supports HTML    yes
experimental features     yes
xmllint enabled           yes

External libraries:
avisynth                libmp3lame              libvorbis
bzlib                   libopencore_amrnb       libvpx
cairo                   libopencore_amrwb       libwebp
gmp                     libopenjpeg             libx264
gnutls                  libopenmpt              libx265
iconv                   libopus                 libxml2
libaom                  librubberband           libxvid
libass                  libspeex                libzimg
libfontconfig           libsrt                  libzmq
libfreetype             libssh                  lzma
libfribidi              libtheora               mediafoundation
libgme                  libvidstab              openal
libgsm                  libvmaf                 sdl2
libharfbuzz             libvo_amrwbenc          zlib

External libraries providing hardware acceleration:
amf                     d3d12va                 nvdec
cuda                    dxva2                   nvenc
cuda_llvm               ffnvcodec               vaapi
cuvid                   libmfx
d3d11va                 libvpl

Libraries:
avcodec                 avformat                swscale
avdevice                avutil
avfilter                swresample

Programs:
ffmpeg                  ffplay                  ffprobe

Enabled decoders:
aac                     flac                    pcm_vidc
aac_fixed               flashsv                 pcx
aac_latm                flashsv2                pdv
aasc                    flic                    pfm
ac3                     flv                     pgm
ac3_fixed               fmvc                    pgmyuv
acelp_kelvin            fourxm                  pgssub
adpcm_4xm               fraps                   pgx
adpcm_adx               frwu                    phm
adpcm_afc               ftr                     photocd
adpcm_agm               g2m                     pictor
adpcm_aica              g723_1                  pixlet
adpcm_argo              g728                    pjs
adpcm_circus            g729                    png
adpcm_ct                gdv                     ppm
adpcm_dtk               gem                     prores
adpcm_ea                gif                     prores_raw
adpcm_ea_maxis_xa       gremlin_dpcm            prosumer
adpcm_ea_r1             gsm                     psd
adpcm_ea_r2             gsm_ms                  ptx
adpcm_ea_r3             h261                    qcelp
adpcm_ea_xas            h263                    qdm2
adpcm_g722              h263i                   qdmc
adpcm_g726              h263p                   qdraw
adpcm_g726le            h264                    qoa
adpcm_ima_acorn         h264_amf                qoi
adpcm_ima_alp           h264_cuvid              qpeg
adpcm_ima_amv           h264_qsv                qtrle
adpcm_ima_apc           hap                     r10k
adpcm_ima_apm           hca                     r210
adpcm_ima_cunning       hcom                    ra_144
adpcm_ima_dat4          hdr                     ra_288
adpcm_ima_dk3           hevc                    ralf
adpcm_ima_dk4           hevc_amf                rasc
adpcm_ima_ea_eacs       hevc_cuvid              rawvideo
adpcm_ima_ea_sead       hevc_qsv                realtext
adpcm_ima_escape        hnm4_video              rka
adpcm_ima_hvqm2         hq_hqa                  rl2
adpcm_ima_hvqm4         hqx                     roq
adpcm_ima_iss           huffyuv                 roq_dpcm
adpcm_ima_magix         hymt                    rpza
adpcm_ima_moflex        iac                     rscc
adpcm_ima_mtf           idcin                   rtv1
adpcm_ima_oki           idf                     rv10
adpcm_ima_pda           iff_ilbm                rv20
adpcm_ima_qt            ilbc                    rv30
adpcm_ima_rad           imc                     rv40
adpcm_ima_smjpeg        imm4                    rv60
adpcm_ima_ssi           imm5                    s302m
adpcm_ima_wav           indeo2                  sami
adpcm_ima_ws            indeo3                  sanm
adpcm_ima_xbox          indeo4                  sbc
adpcm_ms                indeo5                  scpr
adpcm_mtaf              interplay_acm           screenpresso
adpcm_n64               interplay_dpcm          sdx2_dpcm
adpcm_psx               interplay_video         sga
adpcm_psxc              ipu                     sgi
adpcm_sanyo             jacosub                 sgirle
adpcm_sbpro_2           jpeg2000                sheervideo
adpcm_sbpro_3           jpegls                  shorten
adpcm_sbpro_4           jv                      simbiosis_imx
adpcm_swf               kgv1                    sipr
adpcm_thp               kmvc                    siren
adpcm_thp_le            lagarith                smackaud
adpcm_vima              lead                    smacker
adpcm_xa                libaom_av1              smc
adpcm_xmd               libgsm                  smvjpeg
adpcm_yamaha            libgsm_ms               snow
adpcm_zork              libopencore_amrnb       sol_dpcm
agm                     libopencore_amrwb       sp5x
ahx                     libopus                 speedhq
aic                     libspeex                speex
alac                    libvorbis               srgc
alias_pix               libvpx_vp8              srt
als                     libvpx_vp9              ssa
amrnb                   loco                    stl
amrwb                   lscr                    subrip
amv                     m101                    subviewer
anm                     mace3                   subviewer1
ansi                    mace6                   sunrast
anull                   magicyuv                svq1
apac                    mdec                    svq3
ape                     media100                tak
apng                    metasound               targa
aptx                    microdvd                targa_y216
aptx_hd                 mimic                   tdsc
apv                     misc4                   text
arbc                    mjpeg                   theora
argo                    mjpeg_cuvid             thp
ass                     mjpeg_qsv               tiertexseqvideo
asv1                    mjpegb                  tiff
asv2                    mlp                     tmv
atrac1                  mmvideo                 truehd
atrac3                  mobiclip                truemotion1
atrac3al                motionpixels            truemotion2
atrac3p                 movtext                 truemotion2rt
atrac3pal               mp1                     truespeech
atrac9                  mp1float                tscc
aura                    mp2                     tscc2
aura2                   mp2float                tta
av1                     mp3                     twinvq
av1_amf                 mp3adu                  txd
av1_cuvid               mp3adufloat             ulti
av1_qsv                 mp3float                utvideo
avrn                    mp3on4                  v210
avrp                    mp3on4float             v210x
avs                     mpc7                    vb
avui                    mpc8                    vble
bethsoftvid             mpeg1_cuvid             vbn
bfi                     mpeg1video              vc1
bink                    mpeg2_cuvid             vc1_cuvid
binkaudio_dct           mpeg2_qsv               vc1_qsv
binkaudio_rdft          mpeg2video              vc1image
bintext                 mpeg4                   vcr1
bitpacked               mpeg4_cuvid             vmdaudio
bmp                     mpegvideo               vmdvideo
bmv_audio               mpl2                    vmix
bmv_video               msa1                    vmnc
bonk                    mscc                    vnull
brender_pix             msmpeg4v1               vorbis
c93                     msmpeg4v2               vp3
cavs                    msmpeg4v3               vp4
cbd2_dpcm               msnsiren                vp5
ccaption                msp2                    vp6
cdgraphics              msrle                   vp6a
cdtoons                 mss1                    vp6f
cdxl                    mss2                    vp7
cfhd                    msvideo1                vp8
cinepak                 mszh                    vp8_cuvid
clearvideo              mts2                    vp8_qsv
cljr                    mv30                    vp9
cllc                    mvc1                    vp9_amf
comfortnoise            mvc2                    vp9_cuvid
cook                    mvdv                    vp9_qsv
cpia                    mvha                    vplayer
cri                     mwsc                    vqa
cscd                    mxpeg                   vqc
cyuv                    nellymoser              vvc
dca                     notchlc                 vvc_qsv
dds                     nuv                     wady_dpcm
derf_dpcm               on2avc                  wavarc
dfa                     opus                    wavpack
dfpwm                   osq                     wbmp
dirac                   paf_audio               wcmv
dnxhd                   paf_video               webp
dolby_e                 pam                     webp_anim
dpx                     pbm                     webvtt
dsd_lsbf                pcm_alaw                wmalossless
dsd_lsbf_planar         pcm_bluray              wmapro
dsd_msbf                pcm_dvd                 wmav1
dsd_msbf_planar         pcm_f16le               wmav2
dsicinaudio             pcm_f24le               wmavoice
dsicinvideo             pcm_f32be               wmv1
dss_sp                  pcm_f32le               wmv2
dst                     pcm_f64be               wmv3
dvaudio                 pcm_f64le               wmv3image
dvbsub                  pcm_lxf                 wnv1
dvdsub                  pcm_mulaw               wrapped_avframe
dvvideo                 pcm_s16be               ws_snd1
dxa                     pcm_s16be_planar        xan_dpcm
dxtory                  pcm_s16le               xan_wc3
dxv                     pcm_s16le_planar        xan_wc4
eac3                    pcm_s24be               xbin
eacmv                   pcm_s24daud             xbm
eamad                   pcm_s24le               xface
eatgq                   pcm_s24le_planar        xl
eatgv                   pcm_s32be               xma1
eatqi                   pcm_s32le               xma2
eightbps                pcm_s32le_planar        xpm
eightsvx_exp            pcm_s64be               xsub
eightsvx_fib            pcm_s64le               xwd
escape124               pcm_s8                  y41p
escape130               pcm_s8_planar           ylc
evrc                    pcm_sga                 yop
exr                     pcm_u16be               yuv4
fastaudio               pcm_u16le               zero12v
ffv1                    pcm_u24be               zerocodec
ffvhuff                 pcm_u24le               zlib
ffwavesynth             pcm_u32be               zmbv
fic                     pcm_u32le
fits                    pcm_u8

Enabled encoders:
a64multi                hdr                     pcm_s8_planar
a64multi5               hevc_amf                pcm_u16be
aac                     hevc_d3d12va            pcm_u16le
aac_mf                  hevc_mf                 pcm_u24be
ac3                     hevc_nvenc              pcm_u24le
ac3_fixed               hevc_qsv                pcm_u32be
ac3_mf                  hevc_vaapi              pcm_u32le
adpcm_adx               huffyuv                 pcm_u8
adpcm_argo              jpeg2000                pcm_vidc
adpcm_g722              jpegls                  pcx
adpcm_g726              libaom_av1              pdv
adpcm_g726le            libgsm                  pfm
adpcm_ima_alp           libgsm_ms               pgm
adpcm_ima_amv           libmp3lame              pgmyuv
adpcm_ima_apm           libopencore_amrnb       phm
adpcm_ima_qt            libopenjpeg             png
adpcm_ima_ssi           libopus                 ppm
adpcm_ima_wav           libspeex                prores
adpcm_ima_ws            libtheora               prores_aw
adpcm_ms                libvo_amrwbenc          prores_ks
adpcm_swf               libvorbis               qoi
adpcm_yamaha            libvpx_vp8              qtrle
alac                    libvpx_vp9              r10k
alias_pix               libwebp                 r210
amv                     libwebp_anim            ra_144
anull                   libx264                 rawvideo
apng                    libx264rgb              roq
aptx                    libx265                 roq_dpcm
aptx_hd                 libxvid                 rpza
ass                     ljpeg                   rv10
asv1                    magicyuv                rv20
asv2                    mjpeg                   s302m
av1_amf                 mjpeg_qsv               sbc
av1_d3d12va             mjpeg_vaapi             sgi
av1_mf                  mlp                     smc
av1_nvenc               movtext                 snow
av1_qsv                 mp2                     speedhq
av1_vaapi               mp2fixed                srt
avrp                    mp3_mf                  ssa
avui                    mpeg1video              subrip
bitpacked               mpeg2_qsv               sunrast
bmp                     mpeg2_vaapi             svq1
cfhd                    mpeg2video              targa
cinepak                 mpeg4                   text
cljr                    msmpeg4v2               tiff
comfortnoise            msmpeg4v3               truehd
dca                     msrle                   tta
dfpwm                   msvideo1                ttml
dnxhd                   nellymoser              utvideo
dpx                     opus                    v210
dvbsub                  pam                     vbn
dvdsub                  pbm                     vc2
dvvideo                 pcm_alaw                vnull
dxv                     pcm_bluray              vorbis
eac3                    pcm_dvd                 vp8_vaapi
exr                     pcm_f32be               vp9_qsv
ffv1                    pcm_f32le               vp9_vaapi
ffvhuff                 pcm_f64be               wavpack
fits                    pcm_f64le               wbmp
flac                    pcm_mulaw               webvtt
flashsv                 pcm_s16be               wmav1
flashsv2                pcm_s16be_planar        wmav2
flv                     pcm_s16le               wmv1
g723_1                  pcm_s16le_planar        wmv2
gif                     pcm_s24be               wrapped_avframe
h261                    pcm_s24daud             xbm
h263                    pcm_s24le               xface
h263p                   pcm_s24le_planar        xsub
h264_amf                pcm_s32be               xwd
h264_d3d12va            pcm_s32le               y41p
h264_mf                 pcm_s32le_planar        yuv4
h264_nvenc              pcm_s64be               zlib
h264_qsv                pcm_s64le               zmbv
h264_vaapi              pcm_s8

Enabled hwaccels:
av1_d3d11va             hevc_vaapi              vc1_vaapi
av1_d3d11va2            mjpeg_nvdec             vp8_nvdec
av1_d3d12va             mjpeg_vaapi             vp8_nvdec_cuarray
av1_dxva2               mpeg1_nvdec             vp8_vaapi
av1_nvdec               mpeg1_nvdec_cuarray     vp9_d3d11va
av1_nvdec_cuarray       mpeg2_d3d11va           vp9_d3d11va2
av1_vaapi               mpeg2_d3d11va2          vp9_d3d12va
h263_vaapi              mpeg2_d3d12va           vp9_dxva2
h264_d3d11va            mpeg2_dxva2             vp9_nvdec
h264_d3d11va2           mpeg2_nvdec             vp9_nvdec_cuarray
h264_d3d12va            mpeg2_nvdec_cuarray     vp9_vaapi
h264_dxva2              mpeg2_vaapi             vvc_vaapi
h264_nvdec              mpeg4_nvdec             wmv3_d3d11va
h264_nvdec_cuarray      mpeg4_nvdec_cuarray     wmv3_d3d11va2
h264_vaapi              mpeg4_vaapi             wmv3_d3d12va
hevc_d3d11va            vc1_d3d11va             wmv3_dxva2
hevc_d3d11va2           vc1_d3d11va2            wmv3_nvdec
hevc_d3d12va            vc1_d3d12va             wmv3_nvdec_cuarray
hevc_dxva2              vc1_dxva2               wmv3_vaapi
hevc_nvdec              vc1_nvdec
hevc_nvdec_cuarray      vc1_nvdec_cuarray

Enabled parsers:
aac                     dvdsub                  mpegaudio
aac_latm                evc                     mpegvideo
ac3                     ffv1                    opus
adx                     flac                    png
ahx                     ftr                     pnm
amr                     g723_1                  prores
apv                     g729                    prores_raw
av1                     gif                     qoi
avs2                    gsm                     rv34
avs3                    h261                    sbc
bmp                     h263                    sipr
cavsvideo               h264                    tak
cook                    hdr                     vc1
cri                     hevc                    vorbis
dca                     ipu                     vp3
dirac                   jpeg2000                vp8
dnxhd                   jpegxl                  vp9
dnxuc                   jpegxs                  vvc
dolby_e                 lcevc                   webp
dpx                     misc4                   xbm
dvaudio                 mjpeg                   xma
dvbsub                  mlp                     xwd
dvd_nav                 mpeg4video

Enabled demuxers:
aa                      idcin                   pcm_f64le
aac                     idf                     pcm_mulaw
aax                     iff                     pcm_s16be
ac3                     ifv                     pcm_s16le
ac4                     ilbc                    pcm_s24be
ace                     image2                  pcm_s24le
acm                     image2_alias_pix        pcm_s32be
act                     image2_brender_pix      pcm_s32le
adf                     image2pipe              pcm_s8
adp                     image_bmp_pipe          pcm_u16be
ads                     image_cri_pipe          pcm_u16le
adx                     image_dds_pipe          pcm_u24be
aea                     image_dpx_pipe          pcm_u24le
afc                     image_exr_pipe          pcm_u32be
aiff                    image_gem_pipe          pcm_u32le
aix                     image_gif_pipe          pcm_u8
alp                     image_hdr_pipe          pcm_vidc…448 tokens truncated…dx
bit                     iss                     segafilm
bitpacked               iv8                     ser
bmv                     ivf                     sga
boa                     ivr                     shorten
bonk                    jacosub                 siff
brstm                   jpegxl_anim             simbiosis_imx
c93                     jv                      sln
caf                     kux                     smacker
cavsvideo               kvag                    smjpeg
cdg                     laf                     smush
cdxl                    lc3                     sol
cine                    libgme                  sox
codec2                  libopenmpt              spdif
codec2raw               live_flv                srt
concat                  lmlm4                   stl
dash                    loas                    str
data                    lrc                     subviewer
daud                    luodat                  subviewer1
dcstr                   lvf                     sup
derf                    lxf                     svag
dfa                     m4v                     svs
dfpwm                   matroska                swf
dhav                    mca                     tak
dirac                   mcc                     tedcaptions
dnxhd                   mgsts                   thp
dsf                     microdvd                threedostr
dsicin                  mjpeg                   tiertexseq
dss                     mjpeg_2000              tmv
dts                     mlp                     truehd
dtshd                   mlv                     tta
dv                      mm                      tty
dvbsub                  mmf                     txd
dvbtxt                  mods                    ty
dxa                     moflex                  usm
ea                      mov                     v210
ea_cdata                mp3                     v210x
eac3                    mpc                     vag
epaf                    mpc8                    vc1
evc                     mpegps                  vc1t
ffmetadata              mpegts                  vividas
filmstrip               mpegtsraw               vivo
fits                    mpegvideo               vmd
flac                    mpjpeg                  vobsub
flic                    mpl2                    voc
flv                     mpsub                   vpk
fourxm                  msf                     vplayer
frm                     msnwc_tcp               vqf
fsb                     msp                     vvc
fwse                    mtaf                    w64
g722                    mtv                     wady
g723_1                  musx                    wav
g726                    mv                      wavarc
g726le                  mvi                     wc3
g728                    mvr                     webm_dash_manifest
g729                    mxf                     webp_anim
gdv                     mxg                     webvtt
genh                    nc                      wsaud
gif                     nistsphere              wsd
gsm                     nsp                     wsvqa
gxf                     nsv                     wtv
h261                    nut                     wv
h263                    nuv                     wve
h264                    obu                     xa
hca                     ogg                     xbin
hcom                    oma                     xmd
hevc                    osq                     xmv
hls                     paf                     xvag
hnm                     pcm_alaw                xwma
hxvs                    pcm_f32be               yop
iamf                    pcm_f32le               yuv4mpegpipe
ico                     pcm_f64be

Enabled muxers:
a64                     h264                    pcm_s24be
ac3                     hash                    pcm_s24le
ac4                     hds                     pcm_s32be
adts                    hevc                    pcm_s32le
adx                     hls                     pcm_s8
aea                     iamf                    pcm_u16be
aiff                    ico                     pcm_u16le
alp                     ilbc                    pcm_u24be
amr                     image2                  pcm_u24le
amv                     image2pipe              pcm_u32be
apm                     ipod                    pcm_u32le
apng                    ircam                   pcm_u8
aptx                    ismv                    pcm_vidc
aptx_hd                 iterm2                  pdv
apv                     ivf                     psp
argo_asf                jacosub                 rawvideo
argo_cvg                kvag                    rcwt
asf                     latm                    rm
asf_stream              lc3                     roq
ass                     lrc                     rso
ast                     m4v                     rtp
au                      matroska                rtp_mpegts
avi                     matroska_audio          rtsp
avif                    mcc                     sap
avm2                    md5                     sbc
avs2                    microdvd                scc
avs3                    mjpeg                   segafilm
bit                     mkvtimestamp_v2         segment
caf                     mlp                     smjpeg
cavsvideo               mmf                     smoothstreaming
codec2                  mov                     sox
codec2raw               mp2                     spdif
crc                     mp3                     spx
dash                    mp4                     srt
data                    mpeg1system             stream_segment
daud                    mpeg1vcd                streamhash
dfpwm                   mpeg1video              sup
dirac                   mpeg2dvd                swf
dnxhd                   mpeg2svcd               tee
dts                     mpeg2video              tg2
dv                      mpeg2vob                tgp
eac3                    mpegts                  truehd
evc                     mpjpeg                  tta
f4v                     mxf                     ttml
ffmetadata              mxf_d10                 uncodedframecrc
fifo                    mxf_opatom              vc1
filmstrip               null                    vc1t
fits                    nut                     voc
flac                    obu                     vvc
flv                     oga                     w64
framecrc                ogg                     wav
framehash               ogv                     webm
framemd5                oma                     webm_chunk
g722                    opus                    webm_dash_manifest
g723_1                  pcm_alaw                webp
g726                    pcm_f32be               webvtt
g726le                  pcm_f32le               whip
gif                     pcm_f64be               wsaud
gsm                     pcm_f64le               wtv
gxf                     pcm_mulaw               wv
h261                    pcm_s16be               yuv4mpegpipe
h263                    pcm_s16le

Enabled protocols:
async                   http                    rtmp
cache                   httpproxy               rtmpe
concat                  https                   rtmps
concatf                 icecast                 rtmpt
crypto                  ipfs_gateway            rtmpte
data                    ipns_gateway            rtmpts
dtls                    libsrt                  rtp
fd                      libssh                  srtp
ffrtmpcrypt             libzmq                  subfile
ffrtmphttp              md5                     tcp
file                    mmsh                    tee
ftp                     mmst                    tls
gopher                  pipe                    udp
gophers                 prompeg                 udplite

Enabled filters:
a3dscope                dcshift                 paletteuse
aap                     dctdnoiz                pan
abench                  ddagrab                 perlin
abitscope               deband                  perms
acompressor             deblock                 perspective
acontrast               decimate                phase
acopy                   deconvolve              photosensitivity
acrossfade              dedot                   pixdesctest
acrossover              deesser                 pixelize
acrusher                deflate                 pixscope
acue                    deflicker               pp7
addroi                  deinterlace_d3d12       premultiply
adeclick                deinterlace_qsv         premultiply_dynamic
adeclip                 deinterlace_vaapi       prewitt
adecorrelate            dejudder                procamp_vaapi
adelay                  delogo                  pseudocolor
adenorm                 denoise_vaapi           psnr
aderivative             deshake                 pullup
adrawgraph              despill                 qp
adrc                    detelecine              random
adynamicequalizer       dialoguenhance          readeia608
adynamicsmooth          dilation                readvitc
aecho                   displace                realtime
aemphasis               doubleweave             remap
aeval                   drawbox                 removegrain
aevalsrc                drawbox_vaapi           removelogo
aexciter                drawgraph               repeatfields
afade                   drawgrid                replaygain
afdelaysrc              drawtext                reverse
afftdn                  drawvg                  rgbashift
afftfilt                drmeter                 rgbtestsrc
afir                    dynaudnorm              roberts
afireqsrc               earwax                  rotate
afirsrc                 ebur128                 rubberband
aformat                 edgedetect              sab
afreqshift              elbg                    scale
afwtdn                  entropy                 scale2ref
agate                   epx                     scale_cuda
agraphmonitor           eq                      scale_d3d11
ahistogram              equalizer               scale_d3d12
aiir                    erosion                 scale_qsv
aintegral               estdif                  scale_vaapi
ainterleave             exposure                scdet
alatency                extractplanes           scharr
alimiter                extrastereo             scroll
allpass                 fade                    segment
allrgb                  feedback                select
allyuv                  fftdnoiz                selectivecolor
aloop                   fftfilt                 sendcmd
alphaextract            field                   separatefields
alphamerge              fieldhint               setdar
amerge                  fieldmatch              setfield
ametadata               fieldorder              setparams
amf_capture             fillborders             setpts
amix                    find_rect               setrange
amovie                  firequalizer            setsar
amplify                 flanger                 settb
amultiply               floodfill               sharpness_vaapi
anequalizer             format                  shear
anlmdn                  fps                     showcqt
anlmf                   framepack               showcwt
anlms                   framerate               showfreqs
anoisesrc               framestep               showinfo
anull                   frc_amf                 showpalette
anullsink               freezedetect            showspatial
anullsrc                freezeframes            showspectrum
apad                    fspp                    showspectrumpic
aperms                  fsync                   showvolume
aphasemeter             gblur                   showwaves
aphaser                 geq                     showwavespic
aphaseshift             gfxcapture              shuffleframes
apsnr                   gradfun                 shufflepixels
apsyclip                gradients               shuffleplanes
apulsator               graphmonitor            sidechaincompress
arealtime               grayworld               sidechaingate
aresample               greyedge                sidedata
areverse                guided                  sierpinski
arls                    haas                    signalstats
arnndn                  haldclut                signature
asdr                    haldclutsrc             silencedetect
asegment                hdcd                    silenceremove
aselect                 headphone               sinc
asendcmd                hflip                   sine
asetnsamples            highpass                siti
asetpts                 highshelf               smartblur
asetrate                hilbert                 smptebars
asettb                  histeq                  smptehdbars
ashowinfo               histogram               sobel
asidedata               hqdn3d                  spectrumsynth
asisdr                  hqx                     speechnorm
asoftclip               hstack                  split
aspectralstats          hstack_qsv              spp
asplit                  hstack_vaapi            sr_amf
ass                     hsvhold                 ssim
astats                  hsvkey                  ssim360
astreamselect           hue                     stereo3d
asubboost               huesaturation           stereotools
asubcut                 hwdownload              stereowiden
asupercut               hwmap                   streamselect
asuperpass              hwupload                subtitles
asuperstop              hwupload_cuda           super2xsai
atadenoise              hysteresis              superequalizer
atempo                  identity                surround
atilt                   idet                    swaprect
atrim                   il                      swapuv
avectorscope            inflate                 tblend
avgblur                 interlace               telecine
avsynctest              interleave              testsrc
axcorrelate             join                    testsrc2
azmq                    kerndeint               thistogram
backgroundkey           kirsch                  threshold
bandpass                lagfun                  thumbnail
bandreject              latency                 thumbnail_cuda
bass                    latticepal              tile
bbox                    lenscorrection          tiltandshift
bench                   libvmaf                 tiltshelf
bilateral               life                    tinterlace
bilateral_cuda          limitdiff               tlut2
biquad                  limiter                 tmedian
bitplanenoise           loop                    tmidequalizer
blackdetect             loudnorm                tmix
blackframe              lowpass                 tonemap
blend                   lowshelf                tonemap_vaapi
blockdetect             lumakey                 tpad
blurdetect              lut                     transpose
bm3d                    lut1d                   transpose_cuda
boxblur                 lut2                    transpose_vaapi
bwdif                   lut3d                   treble
bwdif_cuda              lutrgb                  tremolo
cas                     lutyuv                  trim
ccrepack                mandelbrot              unpremultiply
cellauto                maskedclamp             unsharp
channelmap              maskedmax               untile
channelsplit            maskedmerge             uspp
chorus                  maskedmin               v360
chromahold              maskedthreshold         vaguedenoiser
chromakey               maskfun                 varblur
chromakey_cuda          mcdeint                 vectorscope
chromanr                mcompand                vflip
chromashift             median                  vfrdet
ciescope                mergeplanes             vibrance
codecview               mestimate               vibrato
color                   mestimate_d3d12         vidstabdetect
colorbalance            metadata                vidstabtransform
colorchannelmixer       midequalizer            vif
colorchart              minterpolate            vignette
colorcontrast           mix                     virtualbass
colorcorrect            monochrome              vmafmotion
colordetect             morpho                  volume
colorhold               movie                   volumedetect
colorize                mpdecimate              vpp_amf
colorkey                mptestsrc               vpp_qsv
colorlevels             msad                    vqe_amf
colormap                multiply                vstack
colormatrix             negate                  vstack_qsv
colorspace              nlmeans                 vstack_vaapi
colorspace_cuda         nnedi                   w3fdif
colorspectrum           noformat                waveform
colortemperature        noise                   weave
compand                 normalize               xbr
compensationdelay       null                    xcorrelate
concat                  nullsink                xfade
convolution             nullsrc                 xmedian
convolve                oscilloscope            xpsnr
copy                    overlay                 xstack
corr                    overlay_cuda            xstack_qsv
cover_rect              overlay_qsv             xstack_vaapi
crop                    overlay_vaapi           yadif
cropdetect              owdenoise               yadif_cuda
crossfeed               pad                     yaepblur
crystalizer             pad_cuda                yuvtestsrc
cue                     pad_vaapi               zmq
curves                  pal100bars              zoneplate
datascope               pal75bars               zoompan
dblur                   palettegen              zscale

Enabled bsfs:
aac_adtstoasc           h264_metadata           pcm_rechunk
ahx_to_mp2              h264_mp4toannexb        pgs_frame_merge
apv_metadata            h264_redundant_pps      prores_metadata
av1_frame_merge         hapqa_extract           remove_extradata
av1_frame_split         hevc_metadata           setts
av1_metadata            hevc_mp4toannexb        showinfo
chomp                   imx_dump_header         smpte436m_to_eia608
dca_core                lcevc_merge             text2movsub
dovi_rpu                lcevc_metadata          trace_headers
dovi_split              media100_to_mjpegb      truehd_core
dts2pts                 mjpeg2jpeg              vp9_metadata
dump_extradata          mjpega_dump_header      vp9_raw_reorder
dv_error_marker         mov2textsub             vp9_superframe
eac3_core               mpeg2_metadata          vp9_superframe_split
eia608_to_smpte436m     mpeg4_unpack_bframes    vvc_metadata
evc_frame_merge         noise                   vvc_mp4toannexb
extract_extradata       null
filter_units            opus_metadata

Enabled indevs:
dshow                   lavfi                   vfwcap
gdigrab                 openal

Enabled outdevs:

git-essentials external libraries' versions:

AMF v1.5.2-2-gc35f613
aom v3.14.1-131-g95f2f18a2b
AviSynthPlus v3.7.5-342-gcfdaf8eb
cairo 1.18.5
ffnvcodec n13.1.15.0-1-geddcea9
gsm 1.0.24
lame 3.100
libgme 0.6.6
libopencore-amrnb 0.1.6
libopencore-amrwb 0.1.6
libssh 0.12.0
libtheora v1.2.0
libwebp v1.6.0-195-g733c91e
openal-soft latest
openmpt libopenmpt-0.6.28-36-g73119913
opus v1.6.1-50-g3da9f7a6
rubberband v4.0.0
SDL release-2.32.0-226-g76847b46b
speex Speex-1.2.1-51-g0589522
srt v1.5.6
VAAPI 2.25.0.
vidstab v1.1.1-24-g92bc0b0
vmaf v3.2.0-7-g78e11b52
vo-amrwbenc 0.1.3
vorbis v1.3.7-36-ge3c9861f
VPL 2.17
vpx v1.16.0-176-gade52487a
x264 v0.165.3223
x265 4.2-78-g8a55d60
xvid v1.3.7
zeromq 4.3.5
zimg release-3.0.6-252-g1ad1895
