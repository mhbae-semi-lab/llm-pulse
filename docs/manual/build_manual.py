# -*- coding: utf-8 -*-
"""LLM Pulse 사용자 매뉴얼(PPTX) 생성기.

    python docs/manual/build_manual.py                 # shots/ 의 캡처로 PPTX 생성
    python docs/manual/build_manual.py --refresh-shots SHOT_DIR
        # --demo --screenshot 으로 뽑은 폴더(ko-*.png)에서 필요한 화면만 shots/ 로 복사·축소
        # 예: LLMPULSE_SHOT_SCALE=1.5 LLMPulse.App.exe --demo --screenshot SHOT_DIR

화면은 모두 --demo 가짜 데이터로 찍는다 (실제 사용 기록 · 실데이터를 넣지 않는다).

스크린샷은 `LLMPULSE_SHOT_SCALE=1.5` 로 캡처한 1920x1140(창 1280x760 DIP) 이미지를 가정한다.
말풍선 좌표는 창 기준 DIP(1280x760) 이다.
"""
import os
import sys

from PIL import Image
from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import MSO_ANCHOR, PP_ALIGN
from pptx.util import Inches, Pt

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(HERE, "shots")
ASSETS = os.path.join(HERE, "assets")
CACHE = os.path.join(HERE, ".cache")
OUT = os.path.join(HERE, "LLM-Pulse-User-Manual-ko.pptx")
VERSION = "1.0.0"
DATE = "2026.11.21"

# ---------------------------------------------------------------- 스크린샷 준비
SHOT_NAMES = ["Dashboard", "DashboardScrolled", "Leaderboard", "LeaderboardMismatch", "Category", "CategoryMixWeights",
              "History", "Metrics", "Matching", "Settings", "SettingsMid", "SettingsEnd", "About", "Wizard0", "Wizard1",
              "Wizard2", "QuickPopup", "Model", "Compare", "CompareDomains", "Plans"]


def refresh_shots(src):
    os.makedirs(SHOTS, exist_ok=True)
    for names in (SHOT_NAMES,):
        for n in names:
            im = Image.open(os.path.join(src, f"ko-{n}.png")).convert("RGB")
            if im.width > 1600:
                im = im.resize((1600, round(im.height * 1600 / im.width)), Image.LANCZOS)
            im.save(os.path.join(SHOTS, f"{n}.png"), optimize=True)
            print("shot", n, im.size)


# ---------------------------------------------------------------- 디자인 토큰
INK = RGBColor(0x1F, 0x29, 0x37)
SUB = RGBColor(0x5B, 0x65, 0x73)
BLUE = RGBColor(0x2E, 0x8B, 0xF0)
NAVY = RGBColor(0x0B, 0x12, 0x20)
CARD = RGBColor(0xF3, 0xF5, 0xF8)
LINE = RGBColor(0xD9, 0xDE, 0xE5)
AMBER = RGBColor(0xE5, 0xA0, 0x2D)
AMBER_BG = RGBColor(0xFF, 0xF6, 0xE5)
AMBER_TX = RGBColor(0x7A, 0x4E, 0x00)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)
FONT = "맑은 고딕"
MONO = "Consolas"

prs = Presentation()
prs.slide_width, prs.slide_height = Inches(13.333), Inches(7.5)
TOTAL = 33
state = {"n": 0}


def new_slide():
    state["n"] += 1
    return prs.slides.add_slide(prs.slide_layouts[6])


def box(s, x, y, w, h, fill=None, line=None, shape=MSO_SHAPE.RECTANGLE, radius=None, lw=0.75):
    sh = s.shapes.add_shape(shape, Inches(x), Inches(y), Inches(w), Inches(h))
    if fill is None:
        sh.fill.background()
    else:
        sh.fill.solid()
        sh.fill.fore_color.rgb = fill
    if line is None:
        sh.line.fill.background()
    else:
        sh.line.color.rgb = line
        sh.line.width = Pt(lw)
    sh.shadow.inherit = False
    if radius is not None and shape == MSO_SHAPE.ROUNDED_RECTANGLE:
        sh.adjustments[0] = radius
    return sh


def rbox(s, x, y, w, h, fill=CARD, line=None, radius=0.03, lw=0.75):
    return box(s, x, y, w, h, fill, line, MSO_SHAPE.ROUNDED_RECTANGLE, radius, lw)


def text(s, x, y, w, h, t, size=12, bold=False, color=INK, align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP,
         shape=None, font=FONT, spacing=None):
    tb = shape or s.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_left = tf.margin_right = Inches(0.05)
    tf.margin_top = tf.margin_bottom = Inches(0.02)
    tf.vertical_anchor = anchor
    lines = t if isinstance(t, list) else [t]
    for i, line in enumerate(lines):
        p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        p.alignment = align
        if spacing:
            p.line_spacing = spacing
        r = p.add_run()
        r.text = line
        r.font.size = Pt(size)
        r.font.bold = bold
        r.font.color.rgb = color
        r.font.name = font
    return tb


def num_circle(s, x, y, n, d=0.3, size=11):
    c = box(s, x, y, d, d, fill=BLUE, line=WHITE, shape=MSO_SHAPE.OVAL, lw=1.5)
    text(s, 0, 0, 0, 0, str(n), size, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=c)
    c.text_frame.margin_left = c.text_frame.margin_right = 0
    return c


# ---------------------------------------------------------------- 공통 틀
def header(s, section, title, bullets):
    pill = box(s, 10.2, 0.0, 2.7, 0.34, fill=BLUE, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.3)
    text(s, 0, 0, 0, 0, section, 10, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=pill)
    text(s, 0.45, 0.32, 11.5, 0.6, f"[LLM Pulse] {title}", 28, True)
    box(s, 0.5, 1.0, 0.9, 0.06, fill=BLUE)
    for i, t in enumerate(bullets):
        box(s, 0.55, 1.37 + i * 0.36, 0.07, 0.07, fill=BLUE, shape=MSO_SHAPE.OVAL)
        text(s, 0.72, 1.24 + i * 0.36, 12.1, 0.32, t, 13.5)
    footer(s)


def footer(s, dark=False):
    box(s, 0.5, 7.05, 12.33, 0.01, fill=LINE)
    s.shapes.add_picture(os.path.join(ASSETS, "icon.png"), Inches(0.5), Inches(7.12), Inches(0.22), Inches(0.22))
    text(s, 0.78, 7.1, 6, 0.25, f"LLM Pulse 사용자 매뉴얼  ·  v{VERSION}  ·  배민혁", 9, False, SUB)
    text(s, 11.8, 7.1, 1.03, 0.25, f"{state['n']} / {TOTAL}", 9, True, SUB, PP_ALIGN.RIGHT)


# ---------------------------------------------------------------- 스크린샷 카드
def load_shot(name, crop=None, dip=(1280, 760)):
    """crop: DIP 사각형 (x0,y0,x1,y1). 반환: (경로, DIP 크기, 오프셋)"""
    path = os.path.join(SHOTS, f"{name}.png")
    im = Image.open(path)
    k = im.width / dip[0]
    if not crop:
        return path, dip, (0, 0)
    os.makedirs(CACHE, exist_ok=True)
    x0, y0, x1, y1 = crop
    out = os.path.join(CACHE, f"{name}_{x0}_{y0}_{x1}_{y1}.png")
    im.crop((round(x0 * k), round(y0 * k), round(x1 * k), round(y1 * k))).save(out, optimize=True)
    return out, (x1 - x0, y1 - y0), (x0, y0)


def D(x0, y0, x1, y1, k=1.25):
    """보이는 화면(1024px 기준)에서 읽은 좌표를 창 DIP(1280x760)로 환산한다."""
    return (x0 * k, y0 * k, x1 * k, y1 * k)


def shot_card(s, x, y, w, h, title, name, callouts=(), crop=None, dip=(1280, 760), demo=False, d=0.24):
    rbox(s, x, y, w, h)
    text(s, x + 0.15, y + 0.1, w - 0.3, 0.32, title, 13, True)
    path, (dw, dh), (ox, oy) = load_shot(name, crop, dip)
    bw, bh = w - 0.4, h - 0.62 - (0.2 if demo else 0)
    k = min(bw / dw, bh / dh)
    iw, ih = dw * k, dh * k
    ix, iy = x + (w - iw) / 2, y + 0.5
    box(s, ix - 0.02, iy - 0.02, iw + 0.04, ih + 0.04, fill=WHITE, line=LINE)
    s.shapes.add_picture(path, Inches(ix), Inches(iy), Inches(iw), Inches(ih))
    for n, r in enumerate(callouts, 1):
        x0, y0, x1, y1 = r
        # 대상 영역에 파란 테두리를 두르고, 번호표는 왼쪽 위 모서리에 붙인다
        box(s, ix + (x0 - ox) * k, iy + (y0 - oy) * k, (x1 - x0) * k, (y1 - y0) * k, fill=None, line=BLUE,
            shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.08, lw=1.75)
        cx, cy = x0, y0
        c = box(s, ix + (cx - ox) * k - d / 2, iy + (cy - oy) * k - d / 2, d, d, fill=BLUE, line=WHITE,
                shape=MSO_SHAPE.OVAL, lw=1.25)
        text(s, 0, 0, 0, 0, str(n), 10, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=c)
        c.text_frame.margin_left = c.text_frame.margin_right = 0
    if demo:
        text(s, x + w - 3.0, y + h - 0.27, 2.85, 0.22, "※ 데모(샘플) 데이터로 찍은 화면", 8.5, False, SUB, PP_ALIGN.RIGHT)


def steps_card(s, x, y, w, h, steps, title="사용 순서", tip=None, desc_size=10):
    rbox(s, x, y, w, h)
    text(s, x + 0.15, y + 0.1, w - 0.3, 0.32, title, 13, True)
    n = len(steps)
    top = y + 0.52
    avail = h - 0.52 - (0.62 if tip else 0.15)
    pitch = min(0.8, avail / n)
    for i, (hd, ds) in enumerate(steps):
        yy = top + i * pitch
        num_circle(s, x + 0.2, yy + 0.03, i + 1)
        text(s, x + 0.62, yy - 0.02, w - 0.8, 0.28, hd, 12, True)
        text(s, x + 0.62, yy + 0.24, w - 0.8, pitch - 0.22, ds, desc_size, False, SUB)
    if tip:
        t = rbox(s, x + 0.15, y + h - 0.55, w - 0.3, 0.42, fill=AMBER_BG, line=AMBER, radius=0.15)
        text(s, 0, 0, 0, 0, "Tip  " + tip, 9.5, False, AMBER_TX, PP_ALIGN.LEFT, MSO_ANCHOR.MIDDLE, shape=t)
        t.text_frame.margin_left = Inches(0.12)


CY, CH = 2.45, 4.5      # 카드 영역
LX, LW = 0.5, 7.0       # 왼쪽(스크린샷) 카드
RX, RW = 7.65, 5.18     # 오른쪽(순서) 카드


def shot_steps(section, title, bullets, shot, callouts, steps, tip=None, card_title=None, crop=None, demo=False,
               dip=(1280, 760), desc_size=10):
    s = new_slide()
    header(s, section, title, bullets)
    shot_card(s, LX, CY, LW, CH, card_title or title.split(" ", 1)[-1], shot, callouts, crop, dip, demo)
    steps_card(s, RX, CY, RW, CH, steps, tip=tip, desc_size=desc_size)
    return s


# ================================================================ 1. 표지
def cover():
    s = new_slide()
    s.background.fill.solid()
    s.background.fill.fore_color.rgb = NAVY
    # 맥박선
    pts = [(0, 6.2), (3.2, 6.2), (3.6, 5.9), (4.0, 6.5), (4.45, 4.4), (4.95, 7.0), (5.35, 5.7), (5.7, 6.2),
           (8.0, 6.2), (8.4, 5.95), (8.75, 6.45), (9.1, 5.2), (9.5, 6.9), (9.8, 6.2), (13.333, 6.2)]
    for width, col in ((9, RGBColor(0x12, 0x3A, 0x6B)), (3.5, BLUE)):
        fb = s.shapes.build_freeform(Inches(pts[0][0]), Inches(pts[0][1]))
        fb.add_line_segments([(Inches(px), Inches(py)) for px, py in pts[1:]], close=False)
        ln = fb.convert_to_shape()
        ln.fill.background()
        ln.line.color.rgb = col
        ln.line.width = Pt(width)
        ln.shadow.inherit = False
    s.shapes.add_picture(os.path.join(ASSETS, "icon.png"), Inches(0.8), Inches(0.9), Inches(0.9), Inches(0.9))
    text(s, 0.75, 2.0, 7, 1.0, "LLM Pulse", 54, True, WHITE)
    text(s, 0.8, 3.05, 7, 0.6, "사용자 매뉴얼", 28, False, RGBColor(0x9C, 0xC9, 0xFA))
    box(s, 0.85, 3.8, 0.9, 0.06, fill=BLUE)
    text(s, 0.8, 4.0, 6.2, 0.9, ["벤치마크를 검사 데이터처럼 다루는", "LLM 추천 · 구독 한도 Windows 트레이 앱"], 15, False, RGBColor(0xC9, 0xD3, 0xE0))
    text(s, 0.8, 6.65, 7, 0.3, f"Version {VERSION}   ·   {DATE}   ·   배민혁", 11, False, RGBColor(0x8A, 0x97, 0xA8))
    path, _, _ = load_shot("Dashboard")
    box(s, 6.88, 0.78, 6.7, 3.98, fill=None, line=RGBColor(0x2A, 0x3B, 0x55), lw=1.5)
    s.shapes.add_picture(path, Inches(6.9), Inches(0.8), Inches(6.66), Inches(3.95))


# ================================================================ 2. 목차
def toc():
    s = new_slide()
    pill = box(s, 10.2, 0.0, 2.7, 0.34, fill=BLUE, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.3)
    text(s, 0, 0, 0, 0, "목차", 10, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=pill)
    text(s, 0.45, 0.32, 11.5, 0.6, "목차", 28, True)
    box(s, 0.5, 1.0, 0.9, 0.06, fill=BLUE)
    text(s, 0.55, 1.2, 12, 0.35, "처음 쓰는 분은 1~2번, 필요한 화면은 쪽수를 보세요. 화면은 모두 --demo 가짜 데이터로 찍었습니다.", 13.5, False, SUB)
    groups = [
        ("1", "시작하기", [("3", "LLM Pulse 소개"), ("4", "설치 · 업데이트 · 제거"), ("5", "첫 실행 마법사")]),
        ("2", "보고 고르기", [("6", "화면 구성"), ("7–10", "대시보드 · 바뀐 이유 · Shadow"), ("11–13", "리더보드 · 소스 불일치"),
                           ("14–15", "분야 상세"), ("16", "모델 상세")]),
        ("3", "비교와 내 구독", [("17–18", "모델 비교"), ("19–20", "내 구독"), ("21", "트레이와 빠른 창")]),
        ("4", "기록과 설정", [("22", "변화 기록"), ("23", "지표 정보"), ("24", "모델 매칭"), ("25–27", "설정 · 백업 · 소스 신선도"),
                           ("28", "현장 검증 절차 4단계"), ("29", "단축키 · 정보")]),
        ("5", "부록", [("30", "데이터 출처와 한계"), ("31", "내 데이터 형식 · 오류 메시지"), ("32", "FAQ · 문제 해결")]),
    ]
    pos = [(0.5, 1.9, 3.95, 2.45), (4.69, 1.9, 3.95, 2.45), (8.88, 1.9, 3.95, 2.45),
           (0.5, 4.5, 6.05, 2.4), (6.78, 4.5, 6.05, 2.4)]
    for (no, name, items), (x, y, w, h) in zip(groups, pos):
        rbox(s, x, y, w, h)
        num_circle(s, x + 0.2, y + 0.2, no, 0.38, 13)
        text(s, x + 0.7, y + 0.2, w - 0.9, 0.4, name, 16, True, anchor=MSO_ANCHOR.MIDDLE)
        pitch = min(0.32, (h - 0.85) / len(items))
        for i, (pg, nm) in enumerate(items):
            yy = y + 0.74 + i * pitch
            text(s, x + 0.3, yy, 0.9, 0.3, pg, 11.5, True, BLUE, font=FONT)
            text(s, x + 1.2, yy, w - 1.4, 0.3, nm, 11.5, False, INK)
    footer(s)


# ================================================================ 3. 소개
def intro():
    s = new_slide()
    header(s, "시작하기", "LLM Pulse는 무엇인가요?", [
        "매일 벤치마크·가격을 모아 \"오늘 내 구독 안에서 뭘 쓰면 좋은지\"를 알려 주는 Windows 트레이 앱",
        "관점: 벤치마크를 검사 데이터처럼 — 출처·기준일을 붙이고, 소스끼리 어긋나면 표시하고, 오래된 값은 판정에서 뺌",
        "모든 데이터는 내 PC에만 저장되고, 결제한 구독 안에서만 추천받을 수 있음",
    ])
    cards = [
        ("내 구독 기준", "쓰는 구독만 체크하면 실제로 쓸 수 있는 모델 기준으로 추천하고, API 와의 손익분기를 보여 줍니다."),
        ("검사 데이터처럼", "소스별 데이터 기준일, 90일 넘은 소스 제외, 소스 간 불일치 ⚠, 1위가 바뀐 이유를 함께 보여 줍니다."),
        ("자동 갱신 · 알림", "매일 정해진 시각에 갱신하고, 1위 교체·가격 변동·사용량 한도를 트레이 알림으로 알려 줍니다."),
    ]
    for i, (hd, ds) in enumerate(cards):
        x = 0.5 + i * 4.19
        rbox(s, x, 2.45, 3.95, 1.95)
        num_circle(s, x + 0.2, 2.65, i + 1, 0.36)
        text(s, x + 0.68, 2.64, 3.1, 0.38, hd, 15, True, anchor=MSO_ANCHOR.MIDDLE)
        text(s, x + 0.25, 3.18, 3.5, 1.1, ds, 11, False, SUB)
    text(s, 0.5, 4.62, 6, 0.3, "데이터가 추천이 되기까지", 13, True)
    flow = [
        ("데이터 수집", ["Artificial Analysis", "OpenRouter · Arena · Aider"]),
        ("검사 · 점수", ["기준일 · 불일치 확인", "지표별 0–100, 분야별 가중 합산"]),
        ("추천", ["최고 성능", "가성비 (허용치 이상 중 최저가)"]),
        ("확인 · 알림", ["대시보드 · 리더보드", "트레이 알림"]),
    ]
    w, gap = 2.78, 0.4
    for i, (hd, ln) in enumerate(flow):
        x = 0.5 + i * (w + gap)
        hl = i == 2
        rbox(s, x, 5.0, w, 1.75, fill=WHITE if hl else CARD, line=BLUE if hl else None, radius=0.04, lw=2)
        text(s, x + 0.1, 5.1, w - 0.2, 0.4, hd, 14, True, BLUE if hl else INK, PP_ALIGN.CENTER)
        text(s, x + 0.1, 5.6, w - 0.2, 1.0, ln, 10.5, False, SUB, PP_ALIGN.CENTER)
        if i < 3:
            a = box(s, x + w + 0.06, 5.72, gap - 0.12, 0.34, fill=BLUE, shape=MSO_SHAPE.RIGHT_ARROW)


# ================================================================ 4. 설치
def install():
    s = new_slide()
    header(s, "시작하기", "설치 · 업데이트 · 제거", [
        "Setup.exe 를 실행하면 관리자 권한 없이 설치되고 시작 메뉴에 'LLM Pulse'가 생김",
        "설치판은 새 버전을 조용히 받아 두고 다음 실행 때 적용 (설정에서 끌 수 있음)",
        "제거는 Windows 설정 → 앱. 자동 실행 · 예약 작업도 함께 지우고, 내 데이터는 남김",
    ])
    steps_card(s, 0.5, CY, 6.6, CH, [
        ("Setup.exe 받기", "GitHub Releases 에서 LLMPulse.App-win-Setup.exe (약 81MB)를 받습니다."),
        ("SmartScreen 경고", "코드 서명이 없어 'Windows의 PC 보호' 창이 뜹니다. [추가 정보] → [실행]."),
        ("설치 확인", "%LOCALAPPDATA%\\LLMPulse.App 에 설치되고 시작 메뉴에 'LLM Pulse'가 생깁니다."),
        ("처음 실행", "첫 실행 마법사가 열립니다. 다음 쪽에서 설명합니다."),
    ], title="설치 순서", tip="정보 화면 [업데이트 확인] → [지금 설치하고 다시 시작]으로 바로 업데이트할 수도 있습니다", desc_size=10.5)
    cards = [
        ("설치판", "약 81MB", "Setup.exe · 자동 업데이트 · 시작 메뉴 등록", True),
        ("휴대용 zip", "약 74MB", "풀어서 실행. Lite zip(약 19MB)은 .NET 8 Desktop Runtime 필요", False),
    ]
    for i, (hd, sz, ds, hl) in enumerate(cards):
        x = 7.3 + i * 2.8
        rbox(s, x, CY, 2.65, 2.35, fill=WHITE if hl else CARD, line=BLUE if hl else None, radius=0.04, lw=2)
        text(s, x + 0.1, CY + 0.15, 2.45, 0.4, hd, 15, True, BLUE if hl else INK, PP_ALIGN.CENTER)
        text(s, x + 0.1, CY + 0.7, 2.45, 0.5, sz, 18, True, INK, PP_ALIGN.CENTER)
        text(s, x + 0.2, CY + 1.3, 2.25, 0.95, ds, 10.5, False, SUB, PP_ALIGN.CENTER)
    rbox(s, 7.3, CY + 2.55, 5.45, 1.95)
    text(s, 7.45, CY + 2.65, 5.2, 0.32, "알아 둘 것", 13, True)
    text(s, 7.5, CY + 3.05, 5.1, 1.4, [
        "·  Windows 10 / 11 (64비트)",
        "·  프로그램: %LOCALAPPDATA%\\LLMPulse.App",
        "·  내 데이터: %LOCALAPPDATA%\\LLMPulse  (제거해도 남음)",
        "·  인터넷: 벤치마크 · 구독 목록 · 업데이트 확인에만 사용",
    ], 10.5, False, SUB, spacing=1.15)


# ================================================================ 5. 마법사
def wizard():
    s = new_slide()
    header(s, "시작하기", "첫 실행 마법사", [
        "처음 실행하면 3단계 마법사가 열리며, 지금 건너뛰고 나중에 설정해도 됨",
        "API 키는 Artificial Analysis에서 무료로 발급(하루 100회 호출 한도)",
        "마법사는 '내 구독' 화면의 [설정 마법사 다시 실행]으로 언제든 다시 열 수 있음",
    ])
    items = [
        ("1. 데이터 소스 키", "Wizard0", (268, 130, 1013, 630), "API 키를 붙여넣고 [저장]. 키는 이 PC에서 Windows 사용자별로 암호화(DPAPI)되어 저장됩니다."),
        ("2. 쓰는 구독 선택", "Wizard1", (193, 80, 1087, 680), "ChatGPT·Claude·Cursor 등을 체크합니다. 모델별 세부 설정은 나중에 '내 구독'에서 바꿀 수 있습니다."),
        ("3. 준비 완료", "Wizard2", (343, 180, 939, 580), "'내가 쓸 수 있는 모델만 보기'를 켜 두면 상단 스위치가 켜진 채 시작합니다. 완료와 함께 첫 갱신이 시작됩니다."),
    ]
    for i, (hd, shot, crop, ds) in enumerate(items):
        x = 0.5 + i * 4.19
        rbox(s, x, CY, 3.95, 4.0)
        num_circle(s, x + 0.15, CY + 0.12, i + 1)
        text(s, x + 0.55, CY + 0.1, 3.3, 0.34, hd.split(". ", 1)[1], 13, True, anchor=MSO_ANCHOR.MIDDLE)
        path, _, _ = load_shot(shot, crop)
        iw, ih = 3.65, 2.45
        box(s, x + 0.15 - 0.02, CY + 0.58 - 0.02, iw + 0.04, ih + 0.04, fill=WHITE, line=LINE)
        s.shapes.add_picture(path, Inches(x + 0.15), Inches(CY + 0.58), Inches(iw), Inches(ih))
        text(s, x + 0.2, CY + 0.58 + ih + 0.2, 3.55, 1.5, ds, 11.5, False, SUB)
    tp = rbox(s, 0.5, CY + 4.1, 12.33, 0.4, fill=AMBER_BG, line=AMBER, radius=0.15)
    text(s, 0, 0, 0, 0, "Tip  키가 없어도 앱은 열리지만 데이터가 비어 있습니다. 설정 → Artificial Analysis API 키에서 언제든 입력할 수 있습니다.",
         10.5, False, AMBER_TX, PP_ALIGN.LEFT, MSO_ANCHOR.MIDDLE, shape=tp)
    tp.text_frame.margin_left = Inches(0.15)


# ================================================================ 본문
def screens():
    SEC1 = "화면 둘러보기"
    shot_steps(SEC1, "화면 구성", [
        "왼쪽 메뉴로 8개 화면을 오가고, 위쪽 바는 모든 화면에서 똑같이 쓰임",
        "Ctrl+1…9 로 화면 이동, Ctrl+K 검색, F5 새로고침",
        "맨 아래에는 데이터 출처(Artificial Analysis)가 항상 표시됨",
    ], "Dashboard", [D(10,80,175,340), D(195,43,298,72), D(693,45,782,66), D(795,43,880,68), D(928,43,1015,68)], [
        ("왼쪽 메뉴", "대시보드 · 리더보드 · 지표 정보 · 변화 기록 · 내 구독 · 모델 매칭 · 설정 · 정보"),
        ("검색", "모델·제작사 이름으로 찾기 (Ctrl+K)"),
        ("내 구독만", "켜면 내 구독 안에서 쓸 수 있는 모델만 표시 (Ctrl+P)"),
        ("언어", "English · 한국어 · 中文 · Français"),
        ("새로고침", "데이터를 지금 다시 받기 (F5). 수동 새로고침은 10분에 1회"),
    ], tip="'마지막 갱신' 시각이 검색창 오른쪽에 표시됩니다", card_title="대시보드 (첫 화면)")

    shot_steps(SEC1, "대시보드 ① 오늘의 추천", [
        "열 때마다 \"오늘 뭘 쓰면 좋은지\"를 한 장으로 보여 주는 첫 화면",
        "목적 버튼(코딩·한국어·빠른 응답·종합·싸면서 충분한 것)에 따라 추천이 바뀜",
        "추천 이유는 점수 구성(예: Arena 코딩 59%)으로 설명됨",
    ], "Dashboard", [(246,146,606,170), (244,262,762,300), (244,315,835,681), (709,333,812,368), (853,315,1255,681)], [
        ("요약 줄", "오늘 신규 모델 수와 이전 스냅샷 대비 1위가 바뀐 분야 수"),
        ("목적 버튼", "무엇이 필요한지 고르면 추천이 바뀝니다"),
        ("추천 카드", "최고 모델, 점수·가격, 이유, 그리고 대안 2개"),
        ("조합 조정", "그 목적에 쓰이는 지표의 가중치를 직접 조절"),
        ("무엇이 바뀌었나", "신규 모델 · 1위 변경(이유 포함) · 가격 변동 (이전 스냅샷 대비)"),
    ], tip="내 구독만 켜면 추천 후보가 내 구독 안의 모델로 한정됩니다", card_title="대시보드")

    shot_steps(SEC1, "대시보드 ② 분야별 한눈에", [
        "아래로 스크롤하면 모든 분야의 최고 성능·가성비·내 구독 기준 최고가 표로 나옴",
        "행을 클릭하면 그 분야의 상세 화면으로 이동",
        "오른쪽 7일 추이로 1위 점수가 오르는지 내리는지 확인",
    ], "DashboardScrolled", [D(197,84,335,578), D(340,84,525,578), D(528,84,712,578), D(715,84,900,578), D(903,84,1003,578)], [
        ("분야", "종합 · 코딩 · 속도 · 응답 지연 · 가격 · 긴 컨텍스트 · Arena 분야 등"),
        ("최고 성능", "그 분야 점수 1위 모델과 100만 토큰당 가격"),
        ("가성비", "1위 점수의 90% 이상 중 가장 싼 모델 (비율은 설정에서 변경)"),
        ("내 구독 기준 최고", "내 구독 안에서 쓸 수 있는 가장 좋은 모델"),
        ("7일 추이", "최근 일주일 1위 점수의 변화 (스냅샷이 2개 이상일 때)"),
    ], tip="비어 있는 칸은 해당 조건을 만족하는 모델이 없다는 뜻입니다", card_title="대시보드 아래쪽")

    shot_steps(SEC1, "대시보드 ③ 1위가 바뀐 이유", [
        "어제와 오늘 스냅샷을 비교해, 1위가 바뀐 분야마다 이유를 한 줄로 붙임",
        "지표 값이 바뀌었으면 어떤 지표가 어제 → 오늘 얼마로 바뀌었는지 두 모델 값을 함께 보여 줌",
        "같은 문장이 분야 카드 아래에도 나오며, 공동 1위는 바뀐 것으로 세지 않음",
    ], "Dashboard", [(866,406,1246,494), (866,496,1246,558)], [
        ("바뀐 지표", "예: Arena 코딩 — 새 1위 1468.4→1490, 이전 1위 1477.4→1477.4"),
        ("다른 분야도 같은 방식", "종합 지능은 종합 지능 지수 값의 변화로 설명"),
        ("새 모델 · 데이터 빠짐", "새 1위가 어제는 점수가 없었거나, 이전 1위의 데이터가 빠진 경우"),
        ("계산 기준 변경", "데이터는 그대로인데 오래된 소스 제외 · 가중치 변경으로 바뀐 경우"),
    ], tip="이유는 어제 데이터를 오늘 기준으로 다시 계산해 보고 판단합니다", card_title="대시보드: 무엇이 바뀌었나",
        crop=(850, 312, 1258, 684))

    shot_steps(SEC1, "대시보드 ④ 오래된 소스와 Shadow", [
        "소스 데이터에 적힌 날짜가 90일을 넘으면(예: Aider, 2025-10-03) 그 소스를 점수 계산에서 뺌",
        "뺀 소스를 넣었다면 1위가 누구였는지는 옆 줄에만 보여 주고, 추천은 바꾸지 않음 (Shadow)",
        "설정에서 '오래된 소스도 점수·추천에 사용'을 켜면 다시 포함",
    ], "DashboardScrolled", [(428,150,1064,174), (428,254,1092,278)], [
        ("제외 안내", "어느 소스를 왜 뺐는지 (90일 넘게 갱신 없음)"),
        ("Shadow 1위", "포함했다면 1위가 누구인지. 같으면 '포함해도 1위는 같습니다'"),
        ("다시 포함하기", "설정 → 데이터 소스 상태 → 오래된 소스도 사용"),
        ("Aider 전용 분야", "'코딩 (Aider 폴리글랏)'은 데이터가 모두 빠져 안내만 표시"),
    ], tip="소스별 기준일은 설정의 '데이터 소스 상태'에서 확인합니다", card_title="대시보드: 분야 카드 아래 안내",
        crop=(244, 96, 1256, 392))

    shot_steps(SEC1, "리더보드 ① 보는 법", [
        "모든 모델을 한 표에서 종합 지능·코딩·속도·가격 등 분야 점수(0–100)로 비교",
        "막대 음영은 점수, 숫자는 항상 함께 표시. '—'는 점수 없음(마우스를 올리면 이유)",
        "행을 더블클릭하면 모델 상세가 열림",
    ], "Leaderboard", [D(235,120,395,147), D(198,250,232,575), D(455,250,483,575), D(490,222,562,575), (930,312,1046,719), (930,277,1046,302)], [
        ("제작사", "Anthropic · OpenAI 등 제작사별로 좁혀 보기"),
        ("★ 즐겨찾기", "별을 눌러 자주 보는 모델을 표시"),
        ("NEW 배지", "출시 30일 이내 모델"),
        ("내 구독 ✓", "내 구독에서 쓸 수 있는 모델"),
        ("점수 칸", "0–100 점수 막대. 값이 없으면 '—'"),
        ("열 머리글", "누르면 정렬 (↓ 높은 순), 한 번 더 누르면 반대"),
    ], card_title="리더보드")

    shot_steps(SEC1, "리더보드 ② 찾고 거르기", [
        "가격 상한·컨텍스트 하한·최근 30일 출시로 후보를 좁히고, 선택은 저장됨",
        "즐겨찾기만 · 내 구독만으로 내가 실제로 쓰는 모델만 볼 수 있음",
        "보이는 그대로 CSV로 내보내거나, 모델을 골라 비교 화면으로 이동",
    ], "Leaderboard", [D(209,153,465,182), D(481,153,572,182), D(583,153,742,182), D(752,153,800,182), D(808,153,899,182), D(214,183,368,209)], [
        ("가격 · 컨텍스트", "가격 상한(100만 토큰당 $1·5·15·50), 컨텍스트 128K·200K·1M 이상"),
        ("최근 30일 출시", "새 모델만 걸러 보기"),
        ("즐겨찾기만 · 내 구독만", "내가 관심 있는 모델로 한정"),
        ("컬럼", "표에 보일 열을 고르기"),
        ("CSV 내보내기", "지금 보이는 행을 그대로 파일로 저장"),
        ("비교", "Ctrl/Shift+클릭으로 1–4개를 고르고 [비교]"),
    ], card_title="리더보드: 필터 줄")

    shot_steps(SEC1, "리더보드 ③ 소스 불일치 ⚠", [
        "같은 모델을 두 소스가 크게 다르게 말하면 '불일치' 열에 ⚠ 를 표시 (값을 고르지 않음)",
        "가격: Artificial Analysis 와 OpenRouter 가 2배 이상 차이 (':free' 같은 변종은 제외)",
        "순위: AA 지능 지수 백분위와 Arena 백분위가 40점 이상 차이 (둘 다 있는 모델이 10개 이상일 때)",
    ], "LeaderboardMismatch", [(698,276,764,302), (716,312,766,452), (296,312,422,452), (938,192,998,228)], [
        ("불일치 열", "머리글을 누르면 ⚠ 모델이 위로 모입니다"),
        ("⚠ 에 마우스", "두 값과 차이를 보여 줌 (예: AA $0.53 · OpenRouter $0.09, 502% 차이)"),
        ("확인할 것", "모델 매칭이 맞는지, 원 출처 페이지의 가격 · 날짜"),
        ("컬럼", "불일치 열을 숨기거나 다시 보이기"),
    ], tip="⚠ 는 어느 쪽이 틀렸다는 뜻이 아니라 확인해 보라는 표시입니다", card_title="리더보드: 불일치 열로 정렬")

    shot_steps(SEC1, "분야 상세 ① 순위와 가성비", [
        "분야를 열면 그 분야의 최고 성능 모델과 가성비 모델을 알려 줌",
        "가성비 = 1위 점수의 90% 이상(설정에서 변경) 중 가장 싼 모델",
        "최근 30일 안에 나왔는데 점수가 아직 없는 모델은 주황색 안내로 알려 줌",
    ], "Category", [D(197,138,600,178), D(197,185,600,225), D(205,255,995,290), D(330,368,588,580), D(648,388,998,580), D(614,348,735,370)], [
        ("최고 성능", "점수 1위 모델과 100만 토큰당 가격"),
        ("가성비", "허용치 이상 점수 중 가장 싼 모델과 그 이유"),
        ("점수 없는 신모델", "주황 글씨. 최신 모델은 Arena·Aider 반영이 늦습니다"),
        ("상위 순위", "금색=1위, 초록=내 구독 안, 파랑=그 밖, 흰 테두리=즐겨찾기"),
        ("가격 대비 점수", "점을 누르면 모델 상세. 파란 선 = 파레토 프런티어"),
        ("로그 눈금", "가격 축을 로그로 보기 (기본 켜짐)"),
    ], card_title="분야 상세: 코딩", desc_size=9.5)

    shot_steps(SEC1, "분야 상세 ② 내 조합 만들기", [
        "'내 코딩 조합'처럼 지표를 섞은 분야는 슬라이더로 비중을 직접 정할 수 있음",
        "0이면 그 지표는 빠지고, 퍼센트는 최종 점수에서 차지하는 비중",
        "마음에 드는 가중치는 이름을 붙여 '내 프리셋'으로 저장",
    ], "CategoryMixWeights", [D(400,297,650,505), D(658,297,702,505), D(704,297,726,505), D(196,277,870,293), D(262,525,406,552), D(412,525,517,552)], [
        ("지표 슬라이더", "Arena 코딩 · 종합 지능 · Aider 통과율 · 출력 속도 등 반영 정도"),
        ("비중(%)", "최종 점수에서 그 지표가 차지하는 비율"),
        ("ⓘ 버튼", "그 지표의 뜻과 측정 방법 보기"),
        ("안내 문구", "0이면 이 조합에서 빠집니다"),
        ("프리셋 이름", "현재 가중치에 붙일 이름 입력"),
        ("현재 가중치 저장", "저장하면 '내 프리셋'으로 다시 불러 쓸 수 있음"),
    ], card_title="분야 상세: 이 분야에 사용한 지표", desc_size=9.5)

    shot_steps(SEC1, "모델 상세", [
        "리더보드나 차트에서 모델을 열면 한 모델의 모든 지표와 순위를 볼 수 있음",
        "분야별 점수·순위·추이와 내 구독에서 쓸 수 있는지를 함께 확인",
        "별 버튼으로 즐겨찾기에 넣으면 리더보드에서 따로 모아 볼 수 있음",
    ], "Model", [D(196,88,252,110), D(676,121,714,152), D(196,186,704,244), D(196,268,420,318), D(196,365,712,575), D(612,372,700,575)], [
        ("뒤로 · 리더보드 열기", "이전 화면 또는 리더보드로 이동"),
        ("★ 즐겨찾기", "추가/해제"),
        ("핵심 수치", "블렌디드 가격, 출력 속도(tok/s), 첫 토큰 지연, 컨텍스트"),
        ("내 구독에서 사용 가능 여부", "체크한 구독별로 포함/한도 있음/불가 표시"),
        ("분야별 점수와 순위", "점수 막대와 'N개 중 M위'"),
        ("추이", "일별 스냅샷으로 그린 작은 꺾은선"),
    ], card_title="모델 상세", demo=True, desc_size=9.5)

    SEC2 = "비교와 내 구독"
    shot_steps(SEC2, "모델 비교 ①", [
        "리더보드에서 Ctrl/Shift+클릭으로 1–4개를 고르고 [선택한 N개 비교]",
        "레이더는 여러 분야 점수를, 추이는 지표 하나의 날짜별 변화를 보여 줌",
        "추이는 일별 스냅샷이 2개 이상 쌓여야 그려짐 (매일 갱신하면 채워짐)",
    ], "Compare", [D(196,152,598,482), D(867,125,1012,152), D(885,395,985,470), D(196,487,402,503)], [
        ("분야별 점수 (레이더)", "모델마다 한 색. 바깥쪽일수록 점수가 높음"),
        ("추이 지표", "종합 지능·속도·가격 등 보고 싶은 지표 선택"),
        ("범례", "모델별 색과 표식. 점을 겹쳐 비교"),
        ("데이터 없는 분야", "점수가 없으면 0으로 그려지니 숫자 표를 함께 확인"),
    ], tip="행을 더블클릭하면 해당 모델의 상세가 열립니다", card_title="모델 비교: 레이더와 추이", demo=True)

    shot_steps(SEC2, "모델 비교 ② 전문 분야와 비용", [
        "AA 전문 분야 지수(금융·회계, 전략·운영, 법률, 의료, 엔지니어링, 경제)를 막대로 비교",
        "월 사용량(백만 토큰)을 넣으면 모델별 예상 API 비용이 계산됨",
        "맨 아래 표에서 모든 분야 점수와 원본 값을 숫자로 확인",
    ], "CompareDomains", [D(225,232,1003,442), D(885,243,990,297), D(196,460,655,478), D(196,490,330,520), D(196,556,310,578)], [
        ("전문 분야 막대", "분야마다 모델별 막대를 나란히 비교"),
        ("범례", "막대 색 = 모델"),
        ("막대가 없을 때", "해당 모델은 그 분야 점수가 없다는 뜻"),
        ("월 사용량", "입력:출력 3:1 기준 백만 토큰. 예상 월 API 비용 계산에 사용"),
        ("분야 점수 표", "0–100 점수와 원본 값을 숫자로"),
    ], tip="전문 분야 지수는 Artificial Analysis가 제공하는 값입니다", card_title="모델 비교: 전문 분야", demo=True)

    shot_steps(SEC2, "내 구독 ①", [
        "쓰는 구독만 체크하면 대시보드·리더보드·차트가 쓸 수 있는 모델 기준으로 바뀜",
        "구독 합계를 같은 사용량의 API 비용과 비교해 어느 쪽이 싼지 알려 줌",
        "구독별로 어떤 모델이 포함/한도 있음/불가인지 직접 고칠 수도 있음",
    ], "Plans", [(244,200,822,236), (846,200,1010,234), (244,252,1142,370), (244,382,492,418), (270,448,382,474), (288,502,898,522)], [
        ("프로필", "구독 조합에 이름을 붙여 저장·삭제"),
        ("설정 마법사 다시 실행", "처음 마법사를 다시 열기"),
        ("구독 vs API 비용", "구독 합계와 같은 사용량을 API로 쓸 때의 비용"),
        ("내 월 사용량", "백만 토큰 단위로 입력하면 위 비교가 갱신"),
        ("구독 체크박스", "쓰는 구독만 체크"),
        ("손익분기", "월 구독료로 API 토큰을 얼마나 쓸 수 있는지"),
    ], card_title="내 구독", demo=True, desc_size=9.5)

    shot_steps(SEC2, "내 구독 ② 사용량 세기", [
        "구독마다 한도(예: 월 500회)를 메모해 두면 남은 횟수와 초기화까지 남은 시간이 보임",
        "+1 / −1 버튼이나 전역 단축키 Ctrl+Alt+U 로 사용 1회를 기록",
        "Claude Code는 내 PC의 로컬 기록(읽기 전용)으로 사용량을 자동 집계",
    ], "Plans", [(288,556,852,594), (288,600,382,632), (394,604,672,628), (288,636,1124,646), (682,600,748,632)], [
        ("한도 · 기간 · 메모", "횟수와 기간(5시간·하루·주·월)을 적고 메모(예: Fast requests)"),
        ("−1 / +1", "사용 1회를 기록하거나 취소"),
        ("N회 중 M회 · 초기화까지", "남은 횟수와 초기화까지 남은 시간"),
        ("진행 막대", "사용률. 80%·100%에 도달하면 트레이 알림"),
        ("초기화", "카운트를 0으로 되돌리기"),
    ], tip="설정의 'Claude 사용량 자동 집계'를 켜면 Claude는 직접 세지 않아도 됩니다", card_title="내 구독: 사용량 한도 메모",
        crop=(200, 330, 1130, 760), demo=True)


def tray():
    s = new_slide()
    header(s, "비교와 내 구독", "트레이와 빠른 창", [
        "창을 닫아도 트레이에서 계속 실행되며, 아이콘 한 번 클릭으로 오늘의 추천을 확인",
        "1위 변경·신규 모델·가격 변동·구독 사용량 한도를 알림으로 알려 줌",
        "전역 단축키 Ctrl+Alt+L 로 미니 위젯, Ctrl+Alt+U 로 사용량 +1",
    ])
    shot_card(s, LX, CY, LW, CH, "트레이 빠른 창", "QuickPopup", [(18,12,268,34), (18,40,262,90), (18,98,262,140),
              (18,152,380,208), (130,256,248,288), (258,256,380,288)], dip=(400, 306))
    steps_card(s, RX, CY, RW, CH, [
        ("추천 제목", "현재 목적(예: 코딩)과 '내 구독 안에서' 기준임을 표시"),
        ("추천 모델", "1순위 모델과 점수 · 100만 토큰당 가격"),
        ("대안", "다음 후보 모델 2개"),
        ("구독 사용량", "한도를 적어 둔 구독의 사용량. +1 로 바로 기록"),
        ("지금 새로고침", "빠른 창에서 바로 데이터 갱신"),
        ("LLM Pulse 열기", "본 창으로 이동"),
    ], tip="트레이 아이콘: 클릭=빠른 창, 더블클릭=본 창, 오른쪽 클릭=메뉴")


def records():
    SEC3 = "기록과 설정"
    shot_steps(SEC3, "변화 기록", [
        "갱신할 때마다 스냅샷이 저장되고, 서로 다른 두 날짜를 골라 비교할 수 있음",
        "순위·점수·가격이 어떻게 달라졌는지 모델별로 한 줄씩 표시",
        "아래로 스크롤하면 모델 출시 타임라인도 볼 수 있음",
    ], "History", [D(222,160,336,183), D(380,160,494,183), D(538,160,715,183), D(402,244,482,575), D(503,244,550,575)], [
        ("이전 날짜", "비교의 기준이 되는 날"),
        ("이후 날짜", "비교 대상이 되는 날 (최신)"),
        ("분야", "어느 분야 점수로 비교할지"),
        ("순위", "예: #48 → #156"),
        ("변동", "▼ 빨강 = 하락, ▲ = 상승. 점수와 가격도 같이 표시"),
    ], tip="스냅샷이 서로 다른 두 날짜 이상 쌓여야 비교할 수 있습니다", card_title="변화 기록")

    shot_steps(SEC3, "지표 정보", [
        "점수에 쓰이는 지표가 무엇이고 어떻게 측정되는지 한곳에서 설명",
        "측정 방법·척도·사용 분야·주의사항·출처를 지표마다 확인",
        "다른 화면의 ⓘ 버튼을 누르면 이 화면의 해당 지표로 바로 이동",
    ], "Metrics", [D(212,330,352,353), D(212,380,592,410), D(212,416,447,447), D(212,452,332,482), D(212,490,462,520), D(212,527,548,546)], [
        ("지표 이름", "예: 첫 토큰 지연(TTFT) — 낮을수록 좋음"),
        ("측정 방법", "누가 어떻게 재는지"),
        ("척도", "원본 단위와 0–100 변환 방식 (↑ 높을수록, ↓ 낮을수록 좋음)"),
        ("사용 분야", "이 지표가 들어가는 분야"),
        ("주의사항", "부하·시간대·추론 모델 등 해석상 유의점"),
        ("출처 · 마지막 갱신", "데이터 출처 링크와 갱신 시각"),
    ], card_title="지표 정보", desc_size=9.5)

    shot_steps(SEC3, "모델 매칭", [
        "OpenRouter·Aider·Arena는 이름이 달라, 후보가 정확히 하나일 때만 자동 연결",
        "연결이 안 된 항목은 이 화면에서 직접 이어 주면 가격·컨텍스트·Arena 점수가 채워짐",
        "'매칭 필요' 목록만 모아 보고 하나씩 처리",
    ], "Matching", [D(196,160,330,185), D(196,193,556,221), D(196,228,556,577), D(573,274,1003,300), D(573,297,1003,326)], [
        ("필터", "매칭 필요 · 매칭됨 · 전체"),
        ("소스 모델 검색", "이름으로 목록 좁히기"),
        ("소스 목록", "왼쪽에서 연결할 항목 선택"),
        ("추천", "비슷한 이름 후보와 유사도(%)"),
        ("추천 클릭", "클릭하면 그 모델과 바로 연결"),
    ], tip="잘못 연결했다면 [연결 해제]로 되돌릴 수 있습니다", card_title="모델 매칭")

    shot_steps(SEC3, "설정 ①", [
        "가성비 허용치, API 키, 수동 갱신, 자동 갱신을 이 화면에서 관리",
        "자동 갱신은 Windows 작업 스케줄러에 등록되어 앱이 꺼져 있어도 동작",
        "Windows 시작 시 자동 실행을 켜면 트레이에서 조용히 돌며 알아서 갱신",
    ], "Settings", [D(196,170,452,190), D(196,257,545,285), D(196,370,268,395), D(196,497,492,527), D(203,557,558,578)], [
        ("가성비 허용치", "1위 점수 대비 이 비율 이상이면 가성비 후보 (기본 90%)"),
        ("API 키 저장", "DPAPI로 암호화해 저장. 설정 파일·로그에는 남지 않음"),
        ("데이터 갱신", "수동 새로고침 (10분에 1회 제한으로 API 한도 보호)"),
        ("자동 갱신 시각", "HH:mm 입력 후 [등록]. 해제도 같은 곳에서"),
        ("Windows 시작 시 실행", "로그인하면 트레이로 조용히 시작"),
    ], card_title="설정")

    shot_steps(SEC3, "설정 ②", [
        "창을 닫아도 트레이에서 계속 실행할지, 업데이트를 자동으로 받을지, 어떤 알림을 받을지 정함",
        "Claude 사용량 자동 집계는 로컬 기록만 읽으며 어디로도 전송하지 않음",
        "자동 업데이트는 설치판(Setup.exe)에서만 동작",
    ], "SettingsMid", [(254,138,696,162), (254,175,502,198), (254,212,1000,280), (254,288,608,312), (254,325,540,460)], [
        ("Windows 시작 시 실행", "로그인하면 트레이로 조용히 시작해 알아서 갱신"),
        ("트레이 상주", "창을 닫아도 트레이에서 계속 실행 (자동 갱신 · 알림 유지)"),
        ("Claude 사용량 자동 집계", "내 PC의 ~/.claude/projects 를 읽기 전용으로 읽음"),
        ("업데이트 자동으로 받기", "실행 1분 뒤 새 버전을 받아 두고 다음 실행 때 적용"),
        ("알림", "1위 변경·신규 모델 / 가격 5% 이상 변동 / 구독 사용량 80%·100%"),
    ], card_title="설정: 가운데", desc_size=9.5)

    shot_steps(SEC3, "설정 ③ 백업과 소스 신선도", [
        "백업: 모델 · 이력 · 구독 · 설정을 파일 하나로 저장하고, 그 파일로 되돌림 (API 키는 빠짐)",
        "소스마다 데이터 기준일과 며칠 지났는지를 보여 주고, 90일을 넘으면 주황 경고",
        "오래된 소스를 계산에 다시 넣을지 여기서 정함 (기본 꺼짐)",
    ], "SettingsEnd", [(244,266,530,304), (254,387,488,413), (245,465,998,566), (256,624,704,646), (256,648,780,670)], [
        ("백업 내보내기 · 되돌리기", "되돌리기 전 현재 데이터는 llmpulse.db.before-restore 로 남김"),
        ("오래된 소스도 사용", "켜면 90일 넘은 소스도 점수 · 추천에 넣음"),
        ("소스 카드", "마지막 성공 시각, 성공/오류, 가져온 모델 수"),
        ("데이터 기준일", "소스에 적힌 날짜(Arena 게시일, Aider 항목 날짜) 또는 받아 온 날"),
        ("오래됨 경고", "90일 넘게 새 데이터가 없음 → 계산에서 뺐다는 안내"),
    ], tip="다른 PC로 옮길 때: 백업 내보내기 → 새 PC에서 되돌리기 → API 키만 다시 입력", card_title="설정: 아래쪽", desc_size=9.5)

    verify_flow()


def verify_flow():
    s = new_slide()
    header(s, "기록과 설정", "현장 검증 절차 4단계", [
        "바꾸기 전에 같은 데이터로 확인하고, 새 기준은 옆에서 먼저 돌려 보고, 언제든 되돌린다",
        "반도체 장비 SW 를 바꿀 때 쓰는 순서(재현 → 비교 → Shadow → 전환 · 롤백)를 앱 안에 그대로 둠",
        "아래 기능은 모두 이 앱에 있으며, 데이터는 내 PC 안에서만 움직임",
    ])
    cols = [
        ("재현", "같은 상황을 다시 열기", [
            "--demo 로 예시 데이터를 연다",
            "설정 → 백업에서 되돌리기로 그날의 상태를 다시 본다",
        ]),
        ("비교", "바꾸기 전후를 나란히", [
            "변화 기록: 두 날짜의 순위 · 점수 · 가격",
            "대시보드: 1위가 바뀐 이유 (어제 → 오늘 값)",
            "분야 상세: 가중치를 바꾸면 순위가 즉시 바뀜",
        ]),
        ("Shadow", "새 기준은 옆에서만", [
            "오래된 소스를 뺀 결과로 추천하고, 넣었을 때의 1위는 옆 줄에만",
            "소스 불일치 ⚠ 는 값을 고르지 않고 표시만",
        ]),
        ("전환 · 롤백", "확인 뒤 바꾸고, 언제든 되돌리기", [
            "설정: 오래된 소스도 사용 켜기 / 끄기",
            "분야 상세: 기본값으로 되돌리기 · 내 프리셋",
            "백업 되돌리기 (이전 데이터는 .before-restore 로 보관)",
        ]),
    ]
    w, gap = 2.9, 0.243
    for i, (hd, sub, items) in enumerate(cols):
        x = 0.5 + i * (w + gap)
        hl = i == 2
        rbox(s, x, CY, w, 3.9, fill=WHITE if hl else CARD, line=BLUE if hl else None, radius=0.04, lw=2)
        num_circle(s, x + 0.18, CY + 0.18, i + 1, 0.38, 13)
        text(s, x + 0.66, CY + 0.16, w - 0.8, 0.42, hd, 17, True, BLUE if hl else INK, anchor=MSO_ANCHOR.MIDDLE)
        text(s, x + 0.2, CY + 0.72, w - 0.4, 0.32, sub, 11.5, True, SUB)
        for j, t in enumerate(items):
            yy = CY + 1.2 + j * 0.82
            box(s, x + 0.24, yy + 0.1, 0.07, 0.07, fill=BLUE, shape=MSO_SHAPE.OVAL)
            text(s, x + 0.38, yy, w - 0.55, 0.78, t, 10.5, False, INK)
        if i < 3:
            box(s, x + w + 0.02, CY + 1.75, gap - 0.04, 0.3, fill=BLUE, shape=MSO_SHAPE.RIGHT_ARROW)
    tp = rbox(s, 0.5, CY + 4.05, 12.33, 0.42, fill=AMBER_BG, line=AMBER, radius=0.15)
    text(s, 0, 0, 0, 0, "Tip  오래된 소스를 다시 넣을지 고민될 때: 대시보드의 Shadow 줄을 며칠 지켜보고, 1위가 계속 같으면 켜도 결과는 같습니다.",
         10.5, False, AMBER_TX, PP_ALIGN.LEFT, MSO_ANCHOR.MIDDLE, shape=tp)
    tp.text_frame.margin_left = Inches(0.15)


def shortcuts():
    s = new_slide()
    header(s, "기록과 설정", "단축키 · 정보 화면", [
        "마우스 없이 검색·새로고침·화면 이동을 할 수 있는 단축키 모음",
        "전역 단축키는 다른 프로그램을 쓰는 중에도 동작",
        "정보 화면에서 버전·업데이트 확인·벤치마크 한계·개인정보 안내를 확인",
    ])
    shot_card(s, LX, CY, LW, CH, "정보 화면", "About", [])
    rbox(s, RX, CY, RW, CH)
    text(s, RX + 0.15, CY + 0.1, RW - 0.3, 0.32, "단축키", 13, True)
    keys = [
        ("Ctrl + K", "검색"), ("F5", "새로고침"), ("Ctrl + P", "내 구독만 보기"),
        ("Ctrl + 1…9", "페이지 이동"), ("Ctrl + + / − / 0", "글자 크기 키우기 / 줄이기 / 원래대로"),
        ("Ctrl + Alt + U", "사용량 +1  (전역)"), ("Ctrl + Alt + L", "미니 위젯  (전역)"),
    ]
    for i, (k, d) in enumerate(keys):
        yy = CY + 0.58 + i * 0.45
        glob = "전역" in d
        chip = rbox(s, RX + 0.2, yy, 1.95, 0.34, fill=INK if not glob else BLUE, radius=0.2)
        text(s, 0, 0, 0, 0, k, 10.5, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=chip, font=MONO)
        text(s, RX + 2.3, yy, RW - 2.45, 0.34, d, 11, False, INK, anchor=MSO_ANCHOR.MIDDLE)
    t = rbox(s, RX + 0.15, CY + CH - 0.55, RW - 0.3, 0.42, fill=AMBER_BG, line=AMBER, radius=0.15)
    text(s, 0, 0, 0, 0, "Tip  파란 칩은 어떤 프로그램에서든 동작하는 전역 단축키입니다", 9.5, False, AMBER_TX, PP_ALIGN.LEFT,
         MSO_ANCHOR.MIDDLE, shape=t)
    t.text_frame.margin_left = Inches(0.12)


def appendix():
    s = new_slide()
    header(s, "부록", "데이터 출처와 한계", [
        "점수는 제3자 벤치마크에서 가져오므로 참고용이며, 내 업무 성능과 다를 수 있음",
        "네트워크는 공개 벤치마크, 이 앱 저장소의 구독 목록, 업데이트 확인에만 사용 (사용 기록은 보내지 않음)",
        "표시되는 데이터의 권리는 각 출처에 있고, Artificial Analysis는 출처 표기 조건으로 사용",
    ])
    src = [
        ("Artificial Analysis", "주 소스", "지능·속도·가격. 무료 API 키 필요 (하루 100회, 출처 표기)", True),
        ("OpenRouter", "보강", "가격·컨텍스트 길이. 키 불필요", False),
        ("Arena (옛 LMArena)", "보강", "사람 선호 Elo (코딩·한국어·수학 등). 기준일 = 리더보드 게시일", False),
        ("Aider", "보강", "코딩 폴리글랏 통과율. 2025-10-03 이후 새 항목 없음 → 기본 제외", False),
    ]
    w = 2.95
    for i, (nm, role, ds, hl) in enumerate(src):
        x = 0.5 + i * (w + 0.176)
        rbox(s, x, CY, w, 1.75, fill=WHITE if hl else CARD, line=BLUE if hl else None, radius=0.04, lw=2)
        text(s, x + 0.12, CY + 0.12, w - 0.24, 0.34, nm, 13.5, True, BLUE if hl else INK)
        text(s, x + 0.12, CY + 0.5, w - 0.24, 0.26, role, 10, True, SUB)
        text(s, x + 0.12, CY + 0.8, w - 0.24, 0.9, ds, 10.5, False, SUB)
    rbox(s, 0.5, 4.4, 12.33, 2.55)
    text(s, 0.65, 4.5, 6, 0.32, "알아 둘 한계", 13, True)
    lim = [
        "AA 무료 API는 코딩·에이전트 지수를 비워 내려 줍니다. '코딩'은 Arena 코딩 + Aider, '내 코딩 조합'은 Arena 코딩 + 종합 지능으로 계산합니다.",
        "Aider 리더보드는 2025-10-03 이후 새 항목이 없어 기본으로 점수에서 뺍니다. Arena 는 최신 모델 반영이 늦을 수 있습니다.",
        "'장비 SW' 프리셋은 C# 전용 공개 지표가 없어 Arena 코딩 · Aider(C++ 포함) · Arena 한국어 · 컨텍스트 길이로 근사합니다.",
        "코드 서명이 없어 SmartScreen 경고가 뜨고, 업데이트는 GitHub 공개 릴리스가 있어야 동작합니다. Cursor·ChatGPT 사용량은 수동입니다.",
    ]
    for i, t in enumerate(lim):
        box(s, 0.7, 5.04 + i * 0.5 + 0.1, 0.07, 0.07, fill=BLUE, shape=MSO_SHAPE.OVAL)
        text(s, 0.88, 4.98 + i * 0.5, 11.8, 0.5, t, 11, False, INK)


def data_formats():
    s = new_slide()
    header(s, "부록", "내 데이터 형식 · 오류 메시지", [
        "구독 목록은 데이터 폴더에 plans.user.json 을 두면 내장 · 내려받은 목록 대신 그것을 씀",
        "파일이 깨졌으면 쓰지 않고 이유(JSON 위치 · 몇 번째 플랜인지)를 '내 구독' 화면에 보여 줌",
        "백업 파일 · 소스 오류도 무엇이 잘못됐는지 화면 문장으로 알려 줌",
    ])
    rbox(s, 0.5, CY, 5.9, CH)
    text(s, 0.65, CY + 0.1, 5.6, 0.32, "plans.user.json 예시", 13, True)
    rbox(s, 0.7, CY + 0.52, 5.5, 2.2, fill=NAVY, radius=0.03)
    text(s, 0.85, CY + 0.6, 5.2, 2.05, [
        '[ { "id": "my_plan",',
        '    "monthlyUsd": 20,',
        '    "vendors": ["Anthropic"],',
        '    "include": ["Claude"], "exclude": ["Haiku"],',
        '    "reviewed": "2026-11-21",',
        '    "name": { "ko": "내 플랜", "en": "My plan" } } ]',
    ], 10, False, RGBColor(0xC9, 0xD3, 0xE0), font=MONO)
    text(s, 0.7, CY + 2.85, 5.5, 1.6, [
        "·  id 는 필수. all: true 면 모든 모델(API 종량제)",
        "·  vendors(제작사) · include(이름 포함) · exclude(이름 제외)로 모델을 고름",
        "·  규칙이 하나도 없는 플랜은 어떤 모델과도 맞추지 않음",
        "·  형식은 저장소의 data/plans.json 과 같음",
    ], 10.5, False, SUB, spacing=1.1)
    rbox(s, 6.6, CY, 6.23, CH)
    text(s, 6.75, CY + 0.1, 6.0, 0.32, "화면에 나오는 오류와 뜻", 13, True)
    rows = [
        ("플랜 파일을 사용할 수 없어 … plans.user.json: … LineNumber: 3", "JSON 문법 오류. 위치는 0부터 센 줄 번호"),
        ("plans.user.json: plan #2 has no id.", "두 번째 플랜에 id 가 없음"),
        ("최신 구독 목록을 받지 못해 저장된 목록을 씁니다: HTTP 404", "내려받기 실패. 이전 사본 또는 내장 목록 사용"),
        ("이 파일로는 되돌릴 수 없어 … not an LLM Pulse database.", "이 앱의 백업이 아님. 현재 데이터는 그대로"),
        ("… schema v9 is newer than this app (v5).", "더 새 버전 앱의 백업. 앱을 먼저 업데이트"),
        ("소스 카드: 키 없음 · 거부 · 한도 초과 · 네트워크", "설정 → 데이터 소스 상태에서 원인별 안내"),
    ]
    for i, (msg, mean) in enumerate(rows):
        yy = CY + 0.55 + i * 0.64
        text(s, 6.8, yy, 5.9, 0.3, msg, 9.5, True, INK)
        text(s, 6.8, yy + 0.28, 5.9, 0.3, "→ " + mean, 9.5, False, SUB)


def faq():
    s = new_slide()
    header(s, "부록", "FAQ · 문제 해결", [
        "대부분의 문제는 설정의 '데이터 소스 상태'에서 원인을 구분해서 볼 수 있음",
        "내 데이터는 %LOCALAPPDATA%\\LLMPulse 에 있고, [설정 → 데이터 폴더 열기]로 바로 열림",
        "예기치 않은 오류는 같은 폴더의 crash.log 에 기록됨",
    ])
    qa = [
        ("점수가 '—'로 비어 있어요", "최신 모델은 Arena 반영이 늦습니다. 칸에 마우스를 올리면 이유가 나옵니다."),
        ("새로고침이 안 돼요", "수동은 10분에 1회, AA 무료 키는 24시간 창마다 100회입니다. 설정의 소스 상태를 보세요."),
        ("⚠ 불일치는 데이터가 틀렸다는 뜻인가요?", "아니요. 두 소스 값이 크게 달라 확인해 보라는 표시입니다. 마우스를 올려 두 값을 보세요."),
        ("Aider 가 '오래됨'으로 나와요", "2025-10-03 이후 새 데이터가 없어 기본으로 점수에서 뺍니다. 설정에서 다시 넣을 수 있습니다."),
        ("설치할 때 SmartScreen 경고가 떠요", "코드 서명이 없어서입니다. [추가 정보] → [실행]을 누르세요."),
        ("모델 이름이 연결되지 않아요", "모델 매칭 화면에서 '매칭 필요' 항목을 추천 목록으로 직접 연결하세요."),
        ("다른 PC로 옮기고 싶어요", "설정 → 백업 내보내기, 새 PC에서 백업에서 되돌리기. API 키만 다시 입력합니다."),
        ("매일 자동으로 갱신하고 싶어요", "설정 → 자동 갱신에서 시각을 정하고 [등록]. 앱이 꺼져 있어도 동작합니다."),
        ("Cursor 사용량이 안 세져요", "로컬 기록이 없어 수동입니다. +1 버튼이나 Ctrl+Alt+U 를 쓰세요."),
        ("앱이 갑자기 닫혔어요", "%LOCALAPPDATA%\\LLMPulse\\crash.log 를 확인하세요."),
    ]
    cw, ch = 6.05, 0.82
    for i, (q, a) in enumerate(qa):
        col, row = i % 2, i // 2
        x = 0.5 + col * (cw + 0.23)
        y = CY + row * (ch + 0.1)
        rbox(s, x, y, cw, ch)
        text(s, x + 0.15, y + 0.06, 0.4, 0.3, "Q", 12, True, BLUE)
        text(s, x + 0.5, y + 0.06, cw - 0.65, 0.3, q, 11.5, True)
        text(s, x + 0.5, y + 0.38, cw - 0.65, 0.42, a, 9.5, False, SUB)


def back():
    s = new_slide()
    s.background.fill.solid()
    s.background.fill.fore_color.rgb = NAVY
    pts = [(0, 3.9), (4.5, 3.9), (4.9, 3.6), (5.3, 4.2), (5.75, 2.2), (6.25, 4.8), (6.65, 3.4), (7.0, 3.9), (13.333, 3.9)]
    for width, col in ((9, RGBColor(0x12, 0x3A, 0x6B)), (3.5, BLUE)):
        fb = s.shapes.build_freeform(Inches(pts[0][0]), Inches(pts[0][1]))
        fb.add_line_segments([(Inches(px), Inches(py)) for px, py in pts[1:]], close=False)
        ln = fb.convert_to_shape()
        ln.fill.background()
        ln.line.color.rgb = col
        ln.line.width = Pt(width)
        ln.shadow.inherit = False
    s.shapes.add_picture(os.path.join(ASSETS, "icon.png"), Inches(6.2), Inches(0.9), Inches(0.9), Inches(0.9))
    text(s, 0, 5.1, 13.333, 0.6, "LLM Pulse", 30, True, WHITE, PP_ALIGN.CENTER)
    text(s, 0, 5.75, 13.333, 0.4, "벤치마크를 검사 데이터처럼, 추천은 내 구독 안에서", 13, False, RGBColor(0xC9, 0xD3, 0xE0), PP_ALIGN.CENTER)
    text(s, 0, 6.5, 13.333, 0.3, f"v{VERSION}   ·   만든 사람 배민혁   ·   MIT License   ·   © 2026", 10, False,
         RGBColor(0x8A, 0x97, 0xA8), PP_ALIGN.CENTER)


def main():
    if "--refresh-shots" in sys.argv:
        i = sys.argv.index("--refresh-shots")
        refresh_shots(sys.argv[i + 1])
        return
    cover()
    toc()
    intro()
    install()
    wizard()
    screens()          # 6–20
    tray()             # 21
    records()          # 22–28 (설정 ①–③, 현장 검증 절차 포함)
    shortcuts()        # 29
    appendix()         # 30
    data_formats()     # 31
    faq()              # 32
    back()             # 33
    assert state["n"] == TOTAL, state["n"]
    prs.save(OUT)
    print("saved", OUT)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
