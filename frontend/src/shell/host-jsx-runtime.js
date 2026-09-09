// The second half of the import map — see host-react.js for why each name is spelled
// out rather than star-re-exported.
//
// A module compiled with the automatic JSX runtime imports `react/jsx-runtime`, which
// is a separate specifier from `react` and therefore needs its own entry. Three names
// is the whole surface: `jsx` for a single child, `jsxs` for several, and `Fragment`.
export { jsx, jsxs, Fragment } from "react/jsx-runtime"
