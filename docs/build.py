# -*- coding: utf-8 -*-
"""
docs/template.html + docs/diagrams/*.drawio  ->  docs/index.html

다이어그램을 고치는 방법
  1. docs/diagrams/ 의 .drawio 파일을 draw.io(https://app.diagrams.net 또는 데스크톱 앱)로 열어 편집·저장한다.
  2. 이 스크립트를 실행한다:   python docs/build.py
  3. docs/index.html 이 다시 만들어진다(다이어그램이 HTML 안에 내장된다).

template.html 안의  {{diagram:파일이름|캡션}}  이 다이어그램으로 바뀐다(파일이름은 확장자 없이).
"""
import html
import json
import re
import sys
from pathlib import Path

DOCS = Path(__file__).resolve().parent
TEMPLATE = DOCS / 'template.html'
OUTPUT = DOCS / 'index.html'
DIAGRAMS = DOCS / 'diagrams'

PATTERN = re.compile(r'\{\{diagram:([^|}]+)\|([^}]*)\}\}')


def render(match):
    name, caption = match.group(1).strip(), match.group(2).strip()
    path = DIAGRAMS / f'{name}.drawio'
    if not path.exists():
        sys.exit(f'다이어그램 파일이 없습니다: {path}')

    config = {
        'highlight': '#0000ff',
        'nav': True,
        'resize': True,
        'toolbar': 'zoom layers lightbox',
        'edit': '_blank',
        'xml': path.read_text(encoding='utf-8'),
    }
    data = html.escape(json.dumps(config, ensure_ascii=False), quote=True)
    return (
        f'<figure class="diagram">'
        f'<div class="mxgraph" data-mxgraph="{data}"></div>'
        f'<noscript><p class="fallback">다이어그램은 JavaScript 가 필요합니다.</p></noscript>'
        f'<figcaption>{html.escape(caption)} '
        f'<a href="diagrams/{name}.drawio" download>원본 .drawio 받기</a></figcaption>'
        f'</figure>'
    )


def main():
    source = TEMPLATE.read_text(encoding='utf-8')
    result, count = PATTERN.subn(render, source)
    OUTPUT.write_text(result, encoding='utf-8')
    print(f'{OUTPUT.relative_to(DOCS.parent)}  (다이어그램 {count}개, {len(result) / 1024:.0f} KB)')


if __name__ == '__main__':
    main()
