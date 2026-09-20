import './brand-mark.css';

/** Shared, text-free product mark; the adjacent product name supplies its label. */
export function BrandMark() {
  return <img className="luma-brand-image" src="/brand/luma.png" width="40" height="40" alt="" draggable={false}/>;
}
