// 页面组件（中文注释在前）：JSX 里的回调与条件渲染 😀
import React, { useEffect } from 'react';

export const Page = memo(() => {
  const data = useData();
  useEffect(() => { loadInEffect(); }, []);
  const onSave = () => { saveTop(); };
  return (
    <Panel onClick={() => clickBody()} onClose={handleClose}>
      {data.ok ? <Ok /> : <Fail />}
      {data.items.map(i => <Row key={i} />)}
    </Panel>
  );
});

export default function Other() {
  renderTop();
  return <Btn onPress={() => pressBody()} />;
}
