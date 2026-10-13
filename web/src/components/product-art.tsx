import { useId } from "react";

// Original vector illustrations; no external image services or asset licensing dependencies.
export function ProductArt({
  kind,
  className = "",
}: {
  kind: string;
  className?: string;
}) {
  const id = useId().replace(/:/g, "");
  const ink = `url(#${id}-ink)`;
  const light = `url(#${id}-light)`;
  return (
    <svg
      viewBox="0 0 360 300"
      className={`product-art ${className}`}
      aria-hidden="true"
    >
      <defs>
        <linearGradient id={`${id}-ink`} x1="0" y1="0" x2="1" y2="1">
          <stop stopColor="#555c57" />
          <stop offset=".45" stopColor="#282f2b" />
          <stop offset="1" stopColor="#101d18" />
        </linearGradient>
        <linearGradient id={`${id}-light`} x1="0" y1="0" x2=".8" y2="1">
          <stop stopColor="#fffdf3" />
          <stop offset="1" stopColor="#b9bcb0" />
        </linearGradient>
        <filter
          id={`${id}-shadow`}
          x="-50%"
          y="-50%"
          width="200%"
          height="220%"
        >
          <feGaussianBlur stdDeviation="9" />
        </filter>
        <pattern
          id={`${id}-mesh`}
          width="5"
          height="5"
          patternUnits="userSpaceOnUse"
        >
          <circle cx="2" cy="2" r=".9" fill="#aeb4a7" opacity=".55" />
        </pattern>
      </defs>
      <ellipse
        cx="185"
        cy="260"
        rx="85"
        ry="10"
        fill="#25362b"
        opacity=".16"
        filter={`url(#${id}-shadow)`}
      />
      {kind === "headphones" && (
        <g transform="rotate(-17 180 150)">
          <path
            d="M91 171V128a89 89 0 0 1 178 0v43"
            fill="none"
            stroke="#111d17"
            strokeWidth="24"
          />
          <path
            d="M92 144v-16a88 88 0 0 1 176 0v16"
            fill="none"
            stroke="#687469"
            strokeWidth="14"
          />
          <path
            d="M103 91a84 84 0 0 1 154 0"
            fill="none"
            stroke="#8c998b"
            strokeWidth="13"
            strokeLinecap="round"
          />
          <path d="M87 145v54m186-54v54" stroke="#8a9185" strokeWidth="9" />
          <rect
            x="69"
            y="153"
            width="50"
            height="90"
            rx="24"
            fill={ink}
            stroke="#657263"
            strokeWidth="2"
          />
          <rect x="104" y="160" width="22" height="78" rx="11" fill="#101a15" />
          <rect
            x="240"
            y="153"
            width="51"
            height="90"
            rx="25"
            fill={ink}
            stroke="#657263"
            strokeWidth="2"
          />
          <rect x="232" y="160" width="22" height="78" rx="11" fill="#101a15" />
          <path
            d="M278 177v32"
            stroke="#899584"
            strokeWidth="2"
            strokeLinecap="round"
          />
          <circle cx="278" cy="220" r="2" fill="#b8caaa" />
        </g>
      )}
      {kind === "keyboard" && (
        <g transform="translate(24 74) rotate(-12 150 80)">
          <rect x="0" y="40" width="310" height="141" rx="13" fill="#9eaa9b" />
          <rect
            x="0"
            y="29"
            width="310"
            height="141"
            rx="13"
            fill={light}
            stroke="#adb4a8"
            strokeWidth="2"
          />
          {Array.from({ length: 4 }, (_, row) =>
            Array.from({ length: 11 }, (_, col) => (
              <rect
                key={`${row}-${col}`}
                x={13 + col * 26}
                y={43 + row * 24}
                width="21"
                height="19"
                rx="3"
                fill={
                  col === 0 && row === 0
                    ? "#d98767"
                    : col > 8
                      ? "#627669"
                      : "#f8f8ec"
                }
                stroke="#a7ad9f"
                strokeWidth=".7"
              />
            )),
          )}
          <rect x="65" y="141" width="148" height="16" rx="3" fill="#f8f8ec" />
          <rect x="13" y="141" width="42" height="16" rx="3" fill="#607266" />
          <rect x="224" y="141" width="70" height="16" rx="3" fill="#607266" />
        </g>
      )}
      {kind === "speaker" && (
        <g transform="rotate(-10 180 170)">
          <path
            d="M234 86q42-39 45 8q1 26-28 37"
            fill="none"
            stroke="#957457"
            strokeWidth="8"
          />
          <rect x="99" y="79" width="155" height="171" rx="54" fill={ink} />
          <rect
            x="103"
            y="84"
            width="147"
            height="160"
            rx="50"
            fill={`url(#${id}-mesh)`}
          />
          <ellipse cx="176" cy="91" rx="58" ry="12" fill="#4d5d4b" />
          <path d="M155 89h9m24 0h9m-4-4v8" stroke="#d0d8c8" strokeWidth="2" />
          <rect x="152" y="202" width="49" height="18" rx="4" fill="#182a21" />
          <path d="M169 208v6m6-8v10m6-8v6" stroke="#c5d0bb" strokeWidth="2" />
        </g>
      )}
      {kind === "lamp" && (
        <g>
          <ellipse cx="181" cy="245" rx="72" ry="13" fill="#8a9183" />
          <ellipse cx="181" cy="241" rx="72" ry="13" fill={light} />
          <path
            d="M180 237V107q0-35 27-35"
            fill="none"
            stroke="#626e5d"
            strokeWidth="9"
          />
          <path d="M171 83q26-67 68-31l29 32z" fill={ink} />
          <ellipse cx="218" cy="85" rx="49" ry="10" fill="#ecebcf" />
          <ellipse cx="218" cy="85" rx="37" ry="6" fill="#fff6cb" />
          <circle cx="188" cy="238" r="4" fill="#788c70" />
        </g>
      )}
      {kind === "mouse" && (
        <g transform="rotate(-24 180 160)">
          <path
            d="M116 135c0-101 128-101 128 0v53c0 86-128 86-128 0z"
            fill={ink}
            stroke="#768271"
            strokeWidth="2"
          />
          <path
            d="M180 64v82m-63 0h126"
            fill="none"
            stroke="#111b16"
            strokeWidth="2"
          />
          <rect x="173" y="84" width="14" height="30" rx="7" fill="#94a38b" />
          <path d="M179 90v16" stroke="#55654c" strokeWidth="2" />
          <path
            d="M129 159v44"
            stroke="#667561"
            strokeWidth="3"
            strokeLinecap="round"
          />
        </g>
      )}
      {kind === "powerbank" && (
        <g transform="rotate(-22 180 160)">
          <rect
            x="117"
            y="64"
            width="130"
            height="191"
            rx="25"
            fill="#a6ad9c"
          />
          <rect
            x="110"
            y="58"
            width="130"
            height="191"
            rx="25"
            fill={light}
            stroke="#a9b09e"
          />
          <rect x="146" y="75" width="57" height="28" rx="9" fill="#26382d" />
          <text
            x="174"
            y="95"
            textAnchor="middle"
            fontSize="15"
            fontFamily="monospace"
            fill="#d0e6b8"
          >
            100
          </text>
          <path d="m177 140-15 26h17l-6 25 21-31h-18z" fill="#7e8e71" />
          <rect x="158" y="231" width="32" height="5" rx="2" fill="#6b7b60" />
        </g>
      )}
      {kind === "bag" && (
        <g transform="rotate(9 180 155)">
          <path
            d="M154 70V58q0-25 39-13q14 4 14 25"
            fill="none"
            stroke="#3c5140"
            strokeWidth="9"
          />
          <rect
            x="108"
            y="69"
            width="145"
            height="182"
            rx="36"
            fill={ink}
            stroke="#5a715b"
            strokeWidth="2"
          />
          <path d="M114 113h133" stroke="#9aaa89" strokeWidth="3" />
          <rect
            x="128"
            y="157"
            width="103"
            height="75"
            rx="16"
            fill="#51614c"
          />
          <path d="M137 176h84" stroke="#a8b399" strokeWidth="2" />
          <rect x="168" y="127" width="25" height="15" rx="3" fill="#b3b89b" />
          <path
            d="M103 129v78m156-78v78"
            stroke="#3e513e"
            strokeWidth="10"
            strokeLinecap="round"
          />
        </g>
      )}
      {kind === "earbuds" && (
        <g transform="rotate(-12 180 160)">
          <rect
            x="100"
            y="149"
            width="160"
            height="99"
            rx="39"
            fill={light}
            stroke="#bbc0b2"
            strokeWidth="2"
          />
          <path d="M103 183h153" stroke="#9fa994" />
          <circle cx="180" cy="213" r="3" fill="#5e7857" />
          <path
            d="M129 72q-25-8-25 16t28 13v31q0 16 13 16t12-16V93q0-24-28-21z"
            fill={light}
            stroke="#a8b49f"
          />
          <path
            d="M224 59q25-8 25 16t-28 13v31q0 16-13 16t-12-16V80q0-24 28-21z"
            fill={light}
            stroke="#a8b49f"
          />
        </g>
      )}
      {kind === "controller" && (
        <g transform="rotate(-12 180 160)">
          <path
            d="M118 106q-38 0-51 93q-5 54 34 37l39-39h82l39 39q39 17 34-37q-13-93-51-93z"
            fill={ink}
            stroke="#637360"
            strokeWidth="2"
          />
          <path
            d="M108 130v37m-18-19h37"
            stroke="#a3b294"
            strokeWidth="11"
            strokeLinejoin="round"
          />
          <circle
            cx="145"
            cy="184"
            r="18"
            fill="#17271c"
            stroke="#75866c"
            strokeWidth="3"
          />
          <circle
            cx="218"
            cy="184"
            r="18"
            fill="#17271c"
            stroke="#75866c"
            strokeWidth="3"
          />
          <circle cx="249" cy="137" r="7" fill="#c4caab" />
          <circle cx="268" cy="155" r="7" fill="#d99471" />
          <circle cx="230" cy="155" r="7" fill="#99b5b0" />
          <circle cx="249" cy="173" r="7" fill="#b9a7ca" />
          <circle cx="181" cy="133" r="5" fill="#c4caab" />
        </g>
      )}
    </svg>
  );
}
